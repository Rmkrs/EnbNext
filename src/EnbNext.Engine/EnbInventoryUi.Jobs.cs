using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private (ImmutableArray<EnbNext.Net7.Presentation.Net7JobListEntry> Rows,int Page,int Type,int Sort,int Secondary,int Tertiary,bool Descending,uint? Selected)? _jobLabelRefresh;
    private static bool IsJobRowLabel(string name)=>name.Length>2 && char.IsAsciiDigit(name[^1]) &&
        new[]{"name","level","sponsor","reward"}.Any(prefix=>name.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
    private uint? _jobSelected;
    // 005A2730 selects one row (+CC) and its job id (+D0); 005A2FB0 highlights only that row.
    // 005A2C50 rebuilds the filtered view and re-selects the last row carrying the id.
    private int _jobSelectedRow=-1;
    private (ImmutableArray<EnbNext.Net7.Presentation.Net7JobListEntry> Jobs,int Type,int Sort,int Secondary,int Tertiary,bool Descending)? _jobRowsKey;
    private int _jobType;
    private int _jobSortColumn;
    private int _jobSecondarySort = 1, _jobTertiarySort = 2;
    private bool _jobSortDescending;
    private string _jobReward = "";
    private object? _jobDetailSource;
    private object _jobDetailTextOwner=new();
    private void JobDetailText(string text,object? source)
    {
        //005A3930 replaces wrapped rows through006D0A70 on each reply,
        //including equal-valued replies. Ordinary title clocks remain retained.
        if(!ReferenceEquals(source,_jobDetailSource))
        { _jobDetailSource=source;_jobDetailTextOwner=new(); }
        CharacterWrappedText("WRAP_DETAIL_JNEWS00","jobDescription",text,_jobDetailTextOwner);
    }
    // Retail 005A3FB0 renders the job reply only. Mission lines and cues
    // belong to 005A8350, driven by mission AuxData, even after this panel closes.
    private void JobsPanel()
    {
        TerminalText("title", _jobType switch { 0 => "COMBAT JOBS", 1 => "TRADE JOBS", _ => "EXPLORE JOBS" });
        var rewardLabel = _terminalLabels[_terminalArt].FirstOrDefault(l => l.Properties.GetValueOrDefault("Attach", "").Equals("reward_title", StringComparison.OrdinalIgnoreCase));
        if (rewardLabel is not null) TerminalLabel("reward_title", rewardLabel.Properties.GetValueOrDefault("String0", ""), rewardLabel);
        if (TerminalRect("FRAME_BOX04") is {} rewardSlot) Draw("cargo/FRAME00",rewardSlot);
        for(int i=0;i<3;i++)
            if(TerminalRect($"BUTTON{i:00}") is {} button)
                Draw(_terminalArt+$"/BUTTON{i:00}",button,"jobType/"+i,selected:i==_jobType);
        var rows=JobRows();
        string[] columns=["NAME","LEVEL","SPONSOR","REWARD"];
        for(int i=0;i<columns.Length;i++)
            if(_conversationAnchors.TryGetValue(_terminalArt+"/"+columns[i],out var anchor))
                Hit("jobSort/"+i,new(anchor.X,anchor.Y-3,i==0?175:90,16));
        _terminalScrollMaximum=Math.Max(0,rows.Length-10);
        if(_terminalPage!=(int)_terminalScroll)SetTerminalScroll(_terminalPage);
        else SetTerminalScroll(_terminalScroll);
        UpdateTerminalSliderRepeat();
        var refresh=(_scene?.StationJobs??[],_terminalPage,_jobType,_jobSortColumn,_jobSecondarySort,_jobTertiarySort,_jobSortDescending,_jobSelected);
        if(_jobLabelRefresh!=refresh)
        {
            _jobLabelRefresh=refresh;
            foreach(var scroll in _terminalListScroll.Values)scroll.Clear();
            foreach(var hover in _terminalListHover.Values)hover.Clear();
        }
        for(int i=0;i<10;i++)
        {
            int index=_terminalPage+i;if(index>=rows.Length)break;
            var job=rows[index];
            // 005A3BC0: +44 receives JobList field 0; type is field 1,
            // level is field 3. 005A3090 displays the three supplied strings.
            bool selected=_jobSelectedRow==index;
            TerminalText($"NAME{i+1:00}",job.Text0,selected);
            TerminalText($"LEVEL{i+1:00}",job.Field3.ToString(),selected);
            TerminalText($"SPONSOR{i+1:00}",job.Text1,selected);
            TerminalText($"REWARD{i+1:00}",job.Text2,selected);
            if (selected) _jobReward=job.Text2;
            foreach(string column in new[]{"NAME","LEVEL","SPONSOR","REWARD"})
                TerminalButton($"{column}{i+1:00}",EnbTerminalCommand.JobDescription,job.Field0);
        }
        TerminalSlider(rows.Length);
        var detail=JobDetail(rows);
        // Keep the text batch attached to an on-screen authored mesh.
        if(TerminalRect("ACCEPT") is {} accept)Draw(_terminalArt+"/ACCEPT",accept,enabled:detail.CanAccept && !_busy);
        var acceptLabel=_terminalLabels[_terminalArt].FirstOrDefault(l=>l.Properties.GetValueOrDefault("Attach","").Equals("accept",StringComparison.OrdinalIgnoreCase));
        if(acceptLabel is not null)TerminalLabel("accept",acceptLabel.Properties.GetValueOrDefault("String0",""),acceptLabel,detail.CanAccept?null:new(.4f,.4f,.4f));
        TerminalText("job_title",detail.Title);
        JobDetailText(detail.Body,detail.Source);
        TerminalText("item_name",detail.Reward);
        if(detail.AcceptJob is uint acceptJob)TerminalButton("ACCEPT",EnbTerminalCommand.JobAccept,acceptJob,detail.CanAccept);
    }
    /// <summary>
    /// The jobs of the chosen kind in their sort order. 005A2C50: stable insertion with the last three column choices;
    /// 005A25B0 retains direction and viewport when changing columns; each publication copies the list, and only a
    /// changed list, tab or sort is a 005A2C50 rebuild (it re-selects the last row carrying the selected id).
    /// </summary>
    private EnbNext.Net7.Presentation.Net7JobListEntry[] JobRows()
    {
        var jobs=(_scene?.StationJobs??[]).Where(j=>j.Field1==_jobType);
        var rows=jobs.OrderBy(j=>j,Comparer<EnbNext.Net7.Presentation.Net7JobListEntry>.Create((a,b)=>
        {
            int result=CompareJobColumn(a,b,_jobSortColumn);
            if(result==0)result=CompareJobColumn(a,b,_jobSecondarySort);
            if(result==0)result=CompareJobColumn(a,b,_jobTertiarySort);
            return _jobSortDescending?-Math.Sign(result):result;
        })).ToArray();
        var listed=_scene?.StationJobs??[];
        if(_jobRowsKey is not {} previous || !previous.Jobs.SequenceEqual(listed) || previous.Type!=_jobType ||
            previous.Sort!=_jobSortColumn || previous.Secondary!=_jobSecondarySort || previous.Tertiary!=_jobTertiarySort ||
            previous.Descending!=_jobSortDescending)
        {
            _jobRowsKey=(listed,_jobType,_jobSortColumn,_jobSecondarySort,_jobTertiarySort,_jobSortDescending);
            _jobSelectedRow=_jobSelected is uint id ? Array.FindLastIndex(rows,j=>j.Field0==id) : -1;
            if(_jobSelectedRow<0)_jobSelected=null;
        }
        return rows;
    }
    /// <summary>
    /// What the detail side shows: the failure, the accept reply (005A3FB0: the reply describes admission; trade
    /// success is conditional on cargo requirements; the later mission AuxData owns the notification), the selected
    /// job's description, or nothing; and the job Accept requests, if any.
    /// </summary>
    private (string Title,string Body,object? Source,string Reward,uint? AcceptJob,bool CanAccept) JobDetail(EnbNext.Net7.Presentation.Net7JobListEntry[] rows)
    {
        var description=_scene?.StationJobDescription;
        bool canAccept=_scene?.StationJobRequestPending != true && _jobSelected is uint candidate && description is {StillAvailable:not 0} && description.JobId==candidate &&
            _scene?.StationJobReply?.JobId!=candidate && rows.Any(j=>j.Field0==candidate);
        if (_message is not null)
            return ("Action could not be completed",_message,_message," ",canAccept && _jobSelected is uint retry ? retry : null,canAccept);
        if(_jobSelected is uint selected && _scene?.StationJobReply is { AcceptStatus: <= 3 } reply && reply.JobId==selected)
        {
            string name=description?.JobId==selected?description.Title:"";
            int seconds=unchecked((int)reply.SecondsRemaining);
            int duration=seconds>60?seconds/60:seconds;
            string unit=seconds>60?"minute":"second", plural=duration>1?"s":"";
            var (title,body)=reply.AcceptStatus switch
            {
                0 when _jobType==1 => ("Acquiring Trade Job",$"{name} mission will be added to your journal if you satisfy the cargo requirements of this mission."),
                0 => ("Job Accepted",$"{name} mission added to journal."),
                1 => ("Job no longer available",$"The ({name}) mission has either just been taken by another player or has been retracted by the sponsor."),
                2 => ("Cannot Accept another mission","You already have the maximum number of missions. Please complete them before requesting another job."),
                _ => ("Cannot Accept another mission now.",FormattableString.Invariant($"You have to wait another {duration} {unit}{plural} before your computer is ready to accept another mission."))
            };
            return (title,body,reply," ",null,canAccept);
        }
        if(_jobSelected is uint selectedJob && description?.JobId==selectedJob)
        {
            bool available=description.StillAvailable!=0;
            return (available?description.Title:"Job no longer available",
                available?description.Description:"Sorry, this job has either just been taken by another player or has been retracted by the sponsor",
                description,available?_jobReward:" ",selectedJob,canAccept);
        }
        return (" "," ",null," ",null,canAccept);
    }
    private static int CompareJobColumn(EnbNext.Net7.Presentation.Net7JobListEntry a, EnbNext.Net7.Presentation.Net7JobListEntry b,int column)
    {
        if(column==1)
        {
            string left=a.Field3.ToString(System.Globalization.CultureInfo.InvariantCulture),right=b.Field3.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int length=left.Length.CompareTo(right.Length);
            return length!=0?length:StringComparer.Ordinal.Compare(left,right);
        }
        return column switch {2=>StringComparer.Ordinal.Compare(a.Text1,b.Text1),3=>RewardNumber(a.Text2).CompareTo(RewardNumber(b.Text2)),_=>StringComparer.Ordinal.Compare(a.Text0,b.Text0)};
    }

    private static long RewardNumber(string text)
    {
        string digits=new(text.TrimStart().TakeWhile(c=>char.IsAsciiDigit(c)||c==',').Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits,out long value)?value:0;
    }
}
