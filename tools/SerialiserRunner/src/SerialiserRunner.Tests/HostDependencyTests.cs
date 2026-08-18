using Xunit;

namespace SerialiserRunner.Tests
{
    public class HostDependencyTests
    {
        // BH.Engine.Serialiser.Compute.ISerialise dispatches through C# `dynamic`, so the runtime
        // binder has to resolve the signature of every Serialise overload to build its candidate
        // set. One of them takes System.Drawing.Bitmap. If Bitmap cannot be loaded in this host the
        // bind fails permanently and every call falls through to the generic fallback, which records
        // "cannot be serialised" and writes null: the whole check then fails on every type and
        // method. .NET Framework resolved Bitmap from the GAC for free, net8.0 does not, and the
        // System.Drawing.Common.dll sitting in the BHoM assemblies folder is a type-forwarding
        // facade that resolves and contains nothing. Loaded by name so this fails rather than
        // refusing to compile when the package reference is missing.
        [Fact]
        public void Bitmap_IsResolvable_SoTheDynamicSerialiserBinderCanBind()
        {
            Type? bitmap = Type.GetType("System.Drawing.Bitmap, System.Drawing.Common", throwOnError: false);

            Assert.NotNull(bitmap);
        }

        // Same class of host gap as Bitmap above, and the reason this project targets
        // net8.0-windows rather than net8.0. BHoM oM assemblies reference WPF and WinForms:
        // Revit_X_oM_20NN reaches PresentationCore, and BH.oM.Forms reaches System.Windows.Forms.
        // On a plain net8.0 host those resolve to nothing, so asm.GetTypes() throws
        // ReflectionTypeLoadException, BHoM_Engine's ExtractTypes swallows it, and the entire
        // assembly registers zero types. Measured: 41 types in Revit_MechanicalPlumbing_oM_2022,
        // 4 unloadable, 0 registered. Under net8.0-windows the same assembly returns all 41.
        //
        // Loaded by name so this fails rather than refusing to compile if the TFM is reverted.
        [Theory]
        [InlineData("System.Windows.Forms.Form, System.Windows.Forms")]
        [InlineData("System.Windows.Media.Brush, PresentationCore")]
        [InlineData("System.Windows.Window, PresentationFramework")]
        public void DesktopFrameworkTypes_AreResolvable_SoOmAssembliesCanBeReflectedOver(string typeName)
        {
            Assert.NotNull(Type.GetType(typeName, throwOnError: false));
        }
    }
}
