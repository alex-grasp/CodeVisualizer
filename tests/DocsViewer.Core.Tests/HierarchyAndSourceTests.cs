using DocsViewer.Core;
using DocsViewer.Core.Model;
using DocsViewer.Core.Parsing;

namespace DocsViewer.Core.Tests;

/// <summary>Document hierarchy (Home-Plumbing.md is the parent of Home-Export-Plumbing.md), referenced documents,
/// and the source-file locator.</summary>
public class HierarchyAndSourceTests
{
    [Theory]
    [InlineData("Home-Export-Plumbing.md", "Home-Plumbing.md")]
    [InlineData("Home-AIQuery-Plumbing.md", "Home-Plumbing.md")]
    [InlineData("A-B-C-Plumbing.md", "A-B-Plumbing.md")]
    [InlineData("Home-Plumbing.md", null)]
    [InlineData("AIPanel-Plumbing.md", null)]
    [InlineData("README.md", null)]
    public void Parent_name_is_inferred_from_the_file_name(string file, string? expected)
        => Assert.Equal(expected, DocLibrary.InferParentName(file));

    [Fact]
    public void Front_matter_parent_and_referenced_docs_are_read()
    {
        var md = "---\nsummary: s\nparent: Home-Plumbing.md\n---\n\n# Child\n\nSee [`AIPanel-Plumbing.md`](AIPanel-Plumbing.md) §3 and Home-Plumbing.md. Not a doc: foo.mdx or /path/x.md.\n";
        var g = new MarkdownDocParser().Parse(md, @"C:\docs\Something-Else.md");
        Assert.Equal("Home-Plumbing.md", g.ParentDocName);
        Assert.Equal(new[] { "Home-Plumbing.md", "AIPanel-Plumbing.md" }, g.ReferencedDocs);
    }

    [Fact]
    public void Library_orders_children_under_their_parent_and_resolves_explicit_parents()
    {
        var dir = Directory.CreateTempSubdirectory("docsviewer-hier");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Home-Plumbing.md"), "# Home\n");
            File.WriteAllText(Path.Combine(dir.FullName, "Home-Export-Plumbing.md"), "# Home export\n");
            File.WriteAllText(Path.Combine(dir.FullName, "Home-Filters-Plumbing.md"), "# Home filters\n");
            File.WriteAllText(Path.Combine(dir.FullName, "AIPanel-Plumbing.md"), "---\nparent: Home-AIQuery-Plumbing.md\n---\n# AI panel\n");
            File.WriteAllText(Path.Combine(dir.FullName, "Home-AIQuery-Plumbing.md"), "# Home AI query\n");
            File.WriteAllText(Path.Combine(dir.FullName, "Login-Plumbing.md"), "# Login\n");

            var lib = new DocLibrary();
            lib.LoadFolder(dir.FullName);
            var ordered = lib.OrderedWithDepth().Select(t => (t.Doc.FileName, t.Depth)).ToList();

            Assert.Equal(new[]
            {
                ("Home-Plumbing.md", 0),
                ("Home-AIQuery-Plumbing.md", 1),
                ("AIPanel-Plumbing.md", 2),
                ("Home-Export-Plumbing.md", 1),
                ("Home-Filters-Plumbing.md", 1),
                ("Login-Plumbing.md", 0),
            }, ordered);

            var home = lib.FindDocument("Home-Plumbing")!;
            Assert.Equal(3, lib.ChildrenOf(home).Count);
            Assert.Null(lib.ParentOf(home));
            Assert.Same(lib.FindDocument("Home-AIQuery-Plumbing.md"), lib.ParentOf(lib.FindDocument("AIPanel-Plumbing.md")!));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void A_parent_cycle_does_not_lose_documents()
    {
        var dir = Directory.CreateTempSubdirectory("docsviewer-cycle");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "A.md"), "---\nparent: B.md\n---\n# A\n");
            File.WriteAllText(Path.Combine(dir.FullName, "B.md"), "---\nparent: A.md\n---\n# B\n");
            var lib = new DocLibrary();
            lib.LoadFolder(dir.FullName);
            Assert.Equal(2, lib.OrderedWithDepth().Count);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("`.../SignIn/Services/StaffSignInService.cs`", "SignIn/Services/StaffSignInService.cs")]
    [InlineData(@"Controllers\Api\SignInController.cs", "Controllers/Api/SignInController.cs")]
    [InlineData("./ClientApp/src/main.ts", "ClientApp/src/main.ts")]
    [InlineData("Controllers/LoginController.cs + Infrastructure/Vite/VitePageResolver.cs", "Controllers/LoginController.cs")]
    public void Doc_paths_are_normalised(string input, string expected)
        => Assert.Equal(expected, SourceLocator.Normalize(input));

    [Fact]
    public void Locator_finds_files_directly_by_tail_match_and_prefers_the_longest_matching_tail()
    {
        var root = Directory.CreateTempSubdirectory("docsviewer-src");
        try
        {
            Create(root, "Web/Controllers/Api/SignInController.cs");
            Create(root, "Core/Application/SignIn/Services/StaffSignInService.cs");
            Create(root, "Other/Services/StaffSignInService.cs");
            Create(root, "ClientApp/src/public/login/main.ts");
            Create(root, "ClientApp/src/home/shell/main.ts");
            Create(root, "node_modules/pkg/main.ts");
            Create(root, "docs/Login-Plumbing.md");

            var locator = new SourceLocator(root.FullName);

            // Direct hit relative to the root.
            Assert.EndsWith(Path.Combine("docs", "Login-Plumbing.md"), locator.Resolve("docs/Login-Plumbing.md")!);
            // Path relative to a project folder: found through the index.
            Assert.EndsWith(Path.Combine("Web", "Controllers", "Api", "SignInController.cs"), locator.Resolve("Controllers/Api/SignInController.cs")!);
            // ".../" prefix and two candidates: the longer tail match wins.
            Assert.EndsWith(Path.Combine("Core", "Application", "SignIn", "Services", "StaffSignInService.cs"), locator.Resolve(".../SignIn/Services/StaffSignInService.cs")!);
            // Generic file names resolve by their folder.
            Assert.EndsWith(Path.Combine("public", "login", "main.ts"), locator.Resolve("ClientApp/src/public/login/main.ts")!);
            Assert.EndsWith(Path.Combine("home", "shell", "main.ts"), locator.Resolve("src/home/shell/main.ts")!);
            // Unknown file: null, no exception.
            Assert.Null(locator.Resolve("Nope/Missing.cs"));
            Assert.Null(locator.Resolve(""));
            Assert.Null(locator.Resolve(null));
        }
        finally
        {
            root.Delete(true);
        }

        static void Create(DirectoryInfo root, string relative)
        {
            var full = Path.Combine(root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// " + relative);
        }
    }

    [Fact]
    public void Repo_root_is_the_folder_with_a_git_entry_or_a_solution()
    {
        var root = Directory.CreateTempSubdirectory("docsviewer-root");
        try
        {
            var docs = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "docs"));
            Assert.Null(SourceLocator.FindRepoRoot(docs.FullName) is { } found && found.StartsWith(root.FullName, StringComparison.OrdinalIgnoreCase) ? found : null);
            File.WriteAllText(Path.Combine(root.FullName, "App.slnx"), "<Solution />");
            Assert.Equal(root.FullName, SourceLocator.FindRepoRoot(docs.FullName));
            Directory.CreateDirectory(Path.Combine(root.FullName, "src", ".git"));
            Assert.Equal(Path.Combine(root.FullName, "src"), SourceLocator.FindRepoRoot(docs.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
