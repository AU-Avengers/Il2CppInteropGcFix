using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace Il2CppInteropGcFix;

public static class GcFixes
{
    private static readonly Harmony Harmony = new(GcFixPatcher.Id);
    private static ManualLogSource _log;

    // A bunch of mods already use the #276 Il2CppInterop build.
    public static readonly bool InteropHasGcFix = typeof(INativeClassStruct).GetProperty("HasReferences") != null;

    // only use the write barrier if that Unity version supports it
    public static readonly bool HasWriteBarrier = ResolveWriteBarrier();

    public static void Apply(ManualLogSource log)
    {
        _log = log;
        Harmony.PatchAll(typeof(InjectedClassPatches));
        Harmony.PatchAll(typeof(FieldWriteBarrierPatches));
        Harmony.PatchAll(typeof(FieldWriteWbarrierStubPatch));
    }

    [HarmonyPatch(typeof(ClassInjector), nameof(ClassInjector.RegisterTypeInIl2Cpp), typeof(Type), typeof(RegisterTypeOptions))]
    public static class InjectedClassPatches
    {
        /// <summary>
        ///     Port of https://github.com/BepInEx/Il2CppInterop/pull/276
        /// </summary>
        [HarmonyPostfix]
        public static void InjectedClassReferencesPatch(Type type)
        {
            if (InteropHasGcFix)
                return;
            var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (classPointer == IntPtr.Zero)
                return;

            var references = HasReferences(IL2CPP.il2cpp_class_get_parent(classPointer));
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var field in type.GetFields(flags))
            {
                var fieldType = field.FieldType;
                if (fieldType == typeof(Il2CppStringField))
                    references = true;
                else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Il2CppReferenceField<>))
                    references = true;
                else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Il2CppValueField<>))
                    references |= HasReferences(Il2CppClassPointerStore.GetNativeClassPointer(fieldType.GenericTypeArguments[0]));
            }

            if (references && !SetHasReferences(classPointer))
                _log.LogWarning($"Could not mark {type.FullName} as holding references");
        }

        /// <summary>
        ///     ClassInjector doesn't align fields, and Boehm's GC only finds pointers on 8-byte boundaries,
        ///     this patch makes sure to store its pointers on 8-byte boundaries.
        /// </summary>
        [HarmonyPostfix]
        public static unsafe void AlignInjectedFieldsPatch(Type type)
        {
            var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (classPointer == IntPtr.Zero)
                return;
            var classStruct = UnityVersionHandler.Wrap((Il2CppClass*)classPointer);
            if (classStruct.FieldCount == 0)
                return;

            var parent = IL2CPP.il2cpp_class_get_parent(classPointer);
            var offset = parent == IntPtr.Zero ? 0 : (int)UnityVersionHandler.Wrap((Il2CppClass*)parent).InstanceSize;
            var moved = false;
            for (var i = 0; i < classStruct.FieldCount; i++)
            {
                var field = UnityVersionHandler.Wrap((Il2CppFieldInfo*)((byte*)classStruct.Fields + i * UnityVersionHandler.FieldInfoSize()));
                var fieldClass = IL2CPP.il2cpp_class_from_il2cpp_type((IntPtr)field.Type);
                int size, alignment;
                if (fieldClass != IntPtr.Zero && IL2CPP.il2cpp_class_is_valuetype(fieldClass))
                {
                    uint valueAlignment = 0;
                    size = IL2CPP.il2cpp_class_value_size(fieldClass, ref valueAlignment);
                    alignment = Math.Max(1, (int)valueAlignment);
                }
                else
                {
                    size = alignment = IntPtr.Size;
                }

                offset = Align(offset, alignment);
                moved |= field.Offset != offset;
                field.Offset = offset;
                offset += size;
            }

            // InjectedClassData stays last, and aligned
            classStruct.InstanceSize = (uint)(Align(offset, IntPtr.Size) + IntPtr.Size);
            classStruct.ActualSize = classStruct.InstanceSize;
            if (moved)
                _log.LogDebug($"Aligned injected fields of {type.FullName}");
        }
    }

    /// <summary>
    ///     Port of https://github.com/BepInEx/Il2CppInterop/pull/276
    /// </summary>
    [HarmonyPatch]
    public static class FieldWriteBarrierPatches
    {
        private static readonly AccessTools.FieldRef<Il2CppStringField, Il2CppObjectBase> StringFieldObject =
            AccessTools.FieldRefAccess<Il2CppStringField, Il2CppObjectBase>("_obj");

        private static readonly AccessTools.FieldRef<Il2CppStringField, IntPtr> StringFieldPointer =
            AccessTools.FieldRefAccess<Il2CppStringField, IntPtr>("_fieldPtr");

        public static bool Prepare() => !InteropHasGcFix && HasWriteBarrier;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Il2CppStringField), nameof(Il2CppStringField.Set));
            // shared code for all ref T
            yield return AccessTools.Method(typeof(Il2CppReferenceField<Il2CppObjectBase>), nameof(Il2CppReferenceField<Il2CppObjectBase>.Set));
        }

        public static bool Prefix(object __instance, object value)
        {
            if (__instance is Il2CppStringField stringField)
            {
                Write(StringFieldObject(stringField), StringFieldPointer(stringField), IL2CPP.ManagedStringToIl2Cpp((string)value));
                return false;
            }

            var type = __instance.GetType();
            var obj = (Il2CppObjectBase)AccessTools.Field(type, "_obj").GetValue(__instance);
            var field = (IntPtr)AccessTools.Field(type, "_fieldPtr").GetValue(__instance)!;
            Write(obj, field, (value as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero);
            return false;
        }

        private static void Write(Il2CppObjectBase obj, IntPtr field, IntPtr value)
        {
            var objectPointer = IL2CPP.Il2CppObjectBaseToPtrNotNull(obj);
            WriteField(objectPointer, objectPointer + (int)IL2CPP.il2cpp_field_get_offset(field), value);
        }
    }

    // force the stub to also use the wbarrier for newer unity versions
    [HarmonyPatch(typeof(IL2CPP), nameof(IL2CPP.FieldWriteWbarrierStub))]
    public static class FieldWriteWbarrierStubPatch
    {
        public static bool Prepare() => HasWriteBarrier && !StubHasWriteBarrier();

        public static bool Prefix(IntPtr obj, IntPtr targetAddress, IntPtr value)
        {
            IL2CPP.il2cpp_gc_wbarrier_set_field(obj, targetAddress, value);
            return false;
        }
    }

    private static bool ResolveWriteBarrier()
    {
        try
        {
            Marshal.Prelink(AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.il2cpp_gc_wbarrier_set_field)));
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    // Fixed Il2CppInterop versions call the barrier from the stub itself
    private static bool StubHasWriteBarrier()
        => PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(IL2CPP), nameof(IL2CPP.FieldWriteWbarrierStub)))
            .Any(instruction => instruction.operand is MethodInfo { Name: nameof(IL2CPP.il2cpp_gc_wbarrier_set_field) or "FieldWriteWbarrier" });

    public static unsafe void WriteField(IntPtr obj, IntPtr address, IntPtr value)
    {
        if (HasWriteBarrier)
            IL2CPP.il2cpp_gc_wbarrier_set_field(obj, address, value);
        else
            *(IntPtr*)address = value;
    }

    private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    // UnityVersionHandler.Wrap returns a wrapper that always has a HasReferences property, but the
    // INativeClassStruct interface doesn't declare it (in non-#276), so it's reached through reflection
    private static unsafe PropertyInfo HasReferencesProperty(IntPtr classPointer, out INativeClassStruct classStruct)
    {
        classStruct = UnityVersionHandler.Wrap((Il2CppClass*)classPointer);
        return classStruct.GetType().GetProperty("HasReferences");
    }

    private static bool HasReferences(IntPtr classPointer)
    {
        if (classPointer == IntPtr.Zero)
            return false;
        var property = HasReferencesProperty(classPointer, out var classStruct);
        return property != null ? (bool)property.GetValue(classStruct)! : IL2CPP.il2cpp_class_has_references(classPointer);
    }

    private static bool SetHasReferences(IntPtr classPointer)
    {
        var property = HasReferencesProperty(classPointer, out var classStruct);
        property?.SetValue(classStruct, true);
        return property != null;
    }
}