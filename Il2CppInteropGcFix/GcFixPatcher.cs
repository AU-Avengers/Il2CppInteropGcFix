using BepInEx.Preloader.Core.Patching;

namespace Il2CppInteropGcFix;

[PatcherPluginInfo(Id, "Il2CppInteropGcFix", "1.0.0")]
public class GcFixPatcher : BasePatcher
{
    public const string Id = "com.pietro420.il2cppinteropgcfix";

    public override void Initialize() => GcFixes.Apply(Log);
}