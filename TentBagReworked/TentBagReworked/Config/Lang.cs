namespace TentBagReworked.Config;

public abstract class Lang {
    public static string UnpackError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:unpack-error");
    public static string IllegalItemError(params object[] args) => Vintagestory.API.Config.Lang.Get("tentbag:illegal-item-error", args);
    public static string SolidGroundError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:solid-ground-error");
    public static string ClearAreaError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:clear-area-error");
    public static string UnpackHungerError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:unpack-hunger-error");
    public static string PackHungerError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:pack-hunger-error");
    public static string EmptyBuildError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:empty-build-error");
    public static string WpRemoveError(params object[] _) => Vintagestory.API.Config.Lang.Get("tentbag:wp-remove-error");
}
