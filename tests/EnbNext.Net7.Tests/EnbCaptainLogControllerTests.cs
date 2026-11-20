using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbCaptainLogControllerTests
{
    [Fact]
    public void FailedDraftSurvivesCloseAndOtherCharactersThenRetriesWithoutLosingPageOrText()
    {
        string root=Path.Combine(Path.GetTempPath(),"enb-log-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"block directory creation");
            var owner=new EnbCaptainLogController();
            using var body=new EnbRetailTextEditor(1024,true);
            using var entry=new EnbRetailTextEditor(250);
            owner.Open(new(blocked,1),1,body,entry);
            owner.ReadPage(17,body,entry);body.Text="retained; draft\nsecond line";
            Assert.Throws<IOException>(()=>owner.Save(body.Text));owner.Close();
            Assert.False(owner.IsOpen);
            owner.Open(new(root,2),2,body,entry);Assert.Equal("",body.Text);Assert.Equal(1,owner.Page);
            body.Text="other character";owner.Save(body.Text);owner.Close();
            owner.Open(new(root,1),1,body,entry);
            Assert.Equal(17,owner.Page);Assert.Equal("17",entry.Text);Assert.Equal(3,entry.MaxLength);
            Assert.Equal("retained; draft\nsecond line",body.Text);owner.Save(body.Text);owner.Close();
            owner.Open(new(root,1),1,body,entry);Assert.True(owner.Initialized);
            Assert.Equal("retained; draft\nsecond line",body.Text);
            owner.ReadPage(1000,body,entry);Assert.Equal(999,owner.Page);Assert.False(body.Entered);
            owner.ReadPage(0,body,entry);Assert.Equal(1,owner.Page);
            owner.ReadPage(17,body,entry);Assert.True(body.Entered);
        }
        finally {Directory.Delete(root,true);}
    }
}
