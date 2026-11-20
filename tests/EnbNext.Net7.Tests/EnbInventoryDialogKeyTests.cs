using System.Reflection;
using EnbNext.Engine;
using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbInventoryDialogKeyTests
{
    private static void Set(EnbInventoryUi ui,string name,object value) =>
        typeof(EnbInventoryUi).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(ui,value);

    [Fact]
    public async Task MissionCancellationUsesTheAuthoredOptionKey()
    {
        using var ui=new EnbInventoryUi(()=>123);
        Set(ui,"_interactive",true);
        Set(ui,"_confirmation",new EnbCharacterActionRequest(123,42,0,7));
        Set(ui,"_confirmForfeit",true);
        Set(ui,"_forfeitDialog",new Dictionary<string,string>{{"Option1","Cancel"},{"Key1","Esc"}});
        Assert.True(await ui.ConfirmationKey(27));
        Assert.False(await ui.ConfirmationKey(27));
    }

    [Fact]
    public async Task GroupCancellationUsesAuthoredKeyAndIgnoresUnboundKey()
    {
        using var ui=new EnbInventoryUi(()=>123);
        Set(ui,"_groupLeaveConfirm",true);
        Set(ui,"_groupLeaveDialog",new Dictionary<string,string>{{"Option1","Cancel"},{"Key1","Esc"}});
        await ui.GroupConfirmationKey(13);
        Assert.True(ui.GroupConfirmationOpen);
        await ui.GroupConfirmationKey(27);
        Assert.False(ui.GroupConfirmationOpen);
    }

    [Fact]
    public async Task VendorQuickClickActsOnlyForVendorOrCargoWithoutPendingTransactionOrDialog()
    {
        using var ui=new EnbInventoryUi(()=>123);
        var owner=new EnbStationInteractionRequest(123,10651,0,0,3,7);
        Set(ui,"_interactive",true);Set(ui,"_trading",true);Set(ui,"_conversationOwner",owner);
        int sent=0;ui.PurchaseRequested=_=>{sent++;return Task.FromResult(true);};
        var buy=new EnbInventoryItemRequest(123,0,4,2,1001);
        // 005A0740 starts vendor transactions only from inventory 4 or 1 slots.
        Assert.False(await ui.VendorQuickClick("x",buy with{Inventory=2},EnbNext.Assets.EnbKeyModifiers.Control));
        // 00690AA0 starts nothing while a transaction is pending; the 00588A70 dialog is modal.
        Set(ui,"_busy",true);
        Assert.True(await ui.VendorQuickClick("x",buy,EnbNext.Assets.EnbKeyModifiers.Control));
        Set(ui,"_busy",false);Set(ui,"_tradeConfirmation",new EnbStationPurchaseRequest(owner,buy,true,false,1,1));
        Assert.True(await ui.VendorQuickClick("x",buy,EnbNext.Assets.EnbKeyModifiers.Shift));
        await ui.VendorKey(true);
        Assert.Equal(0,sent);
    }
}
