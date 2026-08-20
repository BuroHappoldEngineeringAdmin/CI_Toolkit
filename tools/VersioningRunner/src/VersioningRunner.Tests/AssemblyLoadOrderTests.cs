using VersioningRunner.Commands;
using Xunit;

namespace VersioningRunner.Tests
{
    // Load order is not cosmetic. ProbeDeclaringType takes its verdict from the first
    // loaded assembly that yields the declaring type, and 42 type names in the fleet are
    // defined by more than one assembly, so the enumeration order decides the
    // classification for those. See CI_Toolkit#161.
    public class AssemblyLoadOrderTests
    {
        private static string[] Names(IEnumerable<string> paths)
            => paths.Select(Path.GetFileName).ToArray()!;

        [Fact]
        public void UnsortedInput_IsOrdered()
        {
            string[] input =
            [
                @"C:\p\Structure_Engine.dll",
                @"C:\p\Acoustic_oM.dll",
                @"C:\p\Revit_Core_Engine_2022.dll",
                @"C:\p\BHoM.dll",
            ];

            Assert.Equal(
                ["Acoustic_oM.dll", "BHoM.dll", "Revit_Core_Engine_2022.dll", "Structure_Engine.dll"],
                Names(RunCommand.OrderForLoad(input)));
        }

        // The discriminating case, and the reason the comparer is named rather than
        // implied. OrdinalIgnoreCase uppercases before comparing, so '_' (0x5F) lands
        // after letters and RevitAPIUI precedes Revit_Adapter. A lowercase-based sort
        // ('_' before 'a') reverses the pair. Both are "case-insensitive"; only one
        // matches what the runner observed on NTFS, and picking the wrong one would
        // change which assembly answers for a contested type.
        [Theory]
        [InlineData("RevitAPIUI.dll", "Revit_Adapter.dll")]
        [InlineData("TestRunner.dll", "Test_Engine.dll")]
        [InlineData("UIFrameworkServices.dll", "UI_Engine.dll")]
        public void UnderscoreSortsAfterLetters_MatchingObservedNtfsOrder(string first, string second)
        {
            // Supplied in the opposite order to the expected result, so a missing sort fails.
            string[] input = [$@"C:\p\{second}", $@"C:\p\{first}"];
            Assert.Equal([first, second], Names(RunCommand.OrderForLoad(input)));
        }

        // Excludes plain Ordinal, which the underscore cases above do not. Measured on the
        // real 132-assembly closure: Ordinal, OrdinalIgnoreCase and a lowercase-based sort
        // all produce different orders, and only OrdinalIgnoreCase reproduces what NTFS
        // returned. This is the pair where Ordinal diverges first.
        [Fact]
        public void ComparerIsOrdinalIgnoreCase_NotOrdinal()
        {
            string[] input = [@"C:\p\Accord.MachineLearning.dll", @"C:\p\Accord.dll"];
            Assert.Equal(["Accord.dll", "Accord.MachineLearning.dll"], Names(RunCommand.OrderForLoad(input)));
        }

        [Fact]
        public void OrderIsIndependentOfInputOrder()
        {
            string[] a = [@"C:\p\B_oM.dll", @"C:\p\A_Engine.dll", @"C:\p\C_Adapter.dll"];
            string[] b = [@"C:\p\C_Adapter.dll", @"C:\p\B_oM.dll", @"C:\p\A_Engine.dll"];

            Assert.Equal(Names(RunCommand.OrderForLoad(a)), Names(RunCommand.OrderForLoad(b)));
        }

        [Fact]
        public void SortsOnFileNameNotFullPath()
        {
            // Directory.GetFiles is not recursive so this cannot arise today, but sorting
            // whole paths would order by directory first and silently change the answer
            // if it ever did.
            string[] input = [@"C:\zzz\A_Engine.dll", @"C:\aaa\B_Engine.dll"];
            Assert.Equal(["A_Engine.dll", "B_Engine.dll"], Names(RunCommand.OrderForLoad(input)));
        }

        [Fact]
        public void EmptyInput_IsEmpty()
            => Assert.Empty(RunCommand.OrderForLoad([]));
    }
}
