using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public Action<uint,bool,int,bool,bool>? InventorySortPersistence { get; set; }
    private void RestoreInventorySort(EnbRetailPlayerOptions? options)
    {
        _sort=options?.InventoryCargoSort??0;_reverse=options?.InventoryCargoReverse??false;
        _vaultSort=options?.InventoryVaultSort??0;_vaultReverse=options?.InventoryVaultReverse??false;
    }
    private void ReportInventorySort(uint owner,bool vault,int mode,bool reverse)
    {
        string name=_assets!.ResolvePlayerOptions(owner).Options.InventorySort(mode).Name;
        LocalMessageRequested?.Invoke(owner,$"{(reverse?"Reverse Sorting":"Sorting")} {(vault?"Vault":"Cargo")} by {name}");
    }
    private void PersistInventorySort(uint owner,bool vault,int mode,bool reverse,bool reverseChanged)
    {
        if(InventorySortPersistence is {} save)save(owner,vault,mode,reverse,reverseChanged);
        else _assets?.SaveInventorySort(owner,vault,mode,reverse,reverseChanged);
    }
}
