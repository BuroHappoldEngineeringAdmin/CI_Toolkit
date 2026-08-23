using NUnit.Framework;

[TestFixture]
public class FileFilterTests
{
    [TestFixture]
    public class IsRelevantFileTests
    {
        [TestCase("code",          "MyClass.cs",            ExpectedResult = true)]
        [TestCase("copyright",     "MyClass.cs",            ExpectedResult = true)]
        [TestCase("documentation", "MyClass.cs",            ExpectedResult = true)]
        [TestCase("code",          "MyClass.CS",            ExpectedResult = true)]   // extension case-insensitive
        [TestCase("code",          "MyProject.csproj",      ExpectedResult = false)]
        [TestCase("code",          "readme.md",             ExpectedResult = false)]
        [TestCase("project",       "MyProject.csproj",                          ExpectedResult = true)]
        [TestCase("project",       "MyProject.CSPROJ",                          ExpectedResult = true)]  // extension case-insensitive
        [TestCase("project",       "AssemblyInfo.cs",                           ExpectedResult = true)]
        [TestCase("project",       "assemblyinfo.cs",                           ExpectedResult = true)]  // filename case-insensitive
        [TestCase("project",       "src/AssemblyInfo.cs",                       ExpectedResult = true)]  // works with a leading path
        [TestCase("project",       "NotAssemblyInfo.cs",                        ExpectedResult = false)]
        [TestCase("project",       "MyClass.cs",                                ExpectedResult = false)]
        [TestCase("project",       ".ci/unit-tests/Foo.Tests.csproj",           ExpectedResult = false)] // under .ci/ — excluded
        [TestCase("project",       "src/Foo.Tests.csproj",                      ExpectedResult = false)] // *.Tests.csproj — excluded
        [TestCase("project",       @".ci\unit-tests\Bar.Tests.csproj",          ExpectedResult = false)] // backslash path, .ci/ excluded
        public bool IsRelevantFile(string checkType, string file)
            => FileFilter.IsRelevantFile(file, checkType);
    }

    [TestFixture]
    public class IsDatasetFileTests
    {
        [TestCase("a/datasets/foo.json",      ExpectedResult = true)]
        [TestCase("a/Datasets/foo.json",      ExpectedResult = true)]  // case-insensitive
        [TestCase("a/DATASETS/foo.json",      ExpectedResult = true)]  // case-insensitive
        [TestCase(@"a\datasets\foo.json",     ExpectedResult = true)]  // backslash separators
        [TestCase("a/datasets/foo.JSON",      ExpectedResult = true)]  // extension case-insensitive
        [TestCase("a/notdatasets/foo.json",   ExpectedResult = true)]  // bare substring match — mirrors BHoMBot
        [TestCase("a/datasets/foo.cs",        ExpectedResult = false)] // wrong extension
        [TestCase("foo.json",                 ExpectedResult = false)] // no "datasets" substring
        [TestCase("datasets/foo.json",        ExpectedResult = true)]  // root-level
        [TestCase("DataSets/foo.json",        ExpectedResult = true)]  // root-level, mixed case
        [TestCase("DataSets/LCA/deep/x.json", ExpectedResult = true)]  // root-level, nested
        public bool IsDatasetFile(string file)
            => FileFilter.IsDatasetFile(file);
    }
}
