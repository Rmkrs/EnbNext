using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _vault, _vaultFilterOpen, _vaultReverse;
    private int _vaultPage, _vaultFilter, _vaultSort;
    public Func<uint,ulong,int,bool,Task<bool>>? VaultSortRequested { get; set; }
    private string _vaultButtonLabel="";
    private IReadOnlyDictionary<string,string>? _vaultControlTips;

    private void Vault()
    {
        var top=Native("vault/FRAME00");
        var bottom=Native("vault/FRAME35");
        CargoLabel("vault/TITLE_VAULT00","vaultTitle");
        Button("vaultFilter","Filter",new(top.X,Native("vault/BUTTON01").Y,60,Native("vault/BUTTON01").Height));
        Draw("vault/BUTTON01",Native("vault/BUTTON01"),"vaultSort");
        CargoLabel("vault/vault_sort_inv","vaultSort");
        if(_vaultControlTips?.GetValueOrDefault("BUTTON01") is {} sortTip)_tips["vaultSort"]=sortTip.Replace("\\n","\n");
        Draw("vault/EXIT",Native("vault/EXIT"),"vaultClose");
        if(_vaultControlTips?.GetValueOrDefault("EXIT") is {} exitTip)_tips["vaultClose"]=exitTip.Replace("\\n","\n");
        var items=(Data?.Vault??[]).OrderBy(i=>i.Slot).ToArray();
        // 657DA0 queues target3 sorting; only subsequent AuxData changes slot order.
        var keys=Data?.VaultSlotKeys??[];
        int count=keys.Length;
        int pages=Math.Max(1,(count+5)/6-5);_vaultPage=Math.Clamp(_vaultPage,0,pages-1);
        for(int i=0;i<36;i++)
        {
            int index=_vaultPage*6+i;
            // Authored frame names run down columns; inventory slots run across rows.
            int key=index<count?keys[index]:-1;
            var item=items.FirstOrDefault(x=>x.Slot==key);
            Slot("vaultItem/"+index,Native("vault/FRAME"+(i%6*6+i/6).ToString("00")),item,filter:_vaultFilterValue,enabled:key>=0);
            if(key>=0)
            {
                _dropSlots["vaultItem/"+index]=(3,key);
                if(item is null)EmptyInventorySlotTooltip("vaultItem/"+index);
            }
        }
        double x=Native("vault/EXIT").X;
        Draw("thumb/v",new(x,top.Top+32,16,bottom.Bottom-top.Top-48),"vaultTrack");
        Draw("up/CLICK_UP",new(x,top.Top+16,16,16),"vaultPrev");
        Draw("down/CLICK_DOWN",new(x,bottom.Bottom-16,16,16),"vaultNext");
        Draw("thumb/v",new(x,top.Top+32+(bottom.Bottom-top.Top-68)*_vaultPage/Math.Max(1d,pages-1),16,20));
        Flush();
        if(!_vaultFilterOpen)return;
        Frame("ITEM_EXAMINATION_MAIN",new(top.X,top.Y,117,159),"vaultFilterFrame");
        for(int i=0;i<EnbItemInformation.Filters.Length;i++)
        {
            string id="vaultFilter/"+i;var bounds=new Rect(top.X+8,top.Y+4+i*12.4,107,12);
            Hit(id,bounds);Text(id,EnbItemInformation.Filters[i],bounds,_hover==id||_vaultFilter==i?new(1,1,0):new(1,1,1),"FONT_SMALL",false);
        }
        Flush();
    }
}
