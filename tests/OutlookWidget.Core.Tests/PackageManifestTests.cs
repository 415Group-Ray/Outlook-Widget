using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Checks on the package manifest, which the schema cannot make.
/// </summary>
/// <remarks>
/// <para>
/// The widget registration lives inside <c>uap3:Properties</c>, and the package manifest schema
/// explicitly does not validate that element's contents beyond requiring well-formed XML. So a
/// misspelled element, a missing screenshot, or a CLSID that disagrees with the provider's own
/// produces a package that builds, signs, and installs — and then a widget that either never
/// appears in the picker or appears and silently fails to activate. There is no error surfaced in
/// the Widgets Board for either.
/// </para>
/// <para>
/// That is the entire justification for these tests. They are cheap, they run on every
/// <c>dotnet test</c>, and each one replaces a failure that would otherwise be found by installing
/// the package, opening the Board, and guessing.
/// </para>
/// </remarks>
public sealed class PackageManifestTests
{
    private const string Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const string Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private const string Uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private const string Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static XDocument Manifest() => XDocument.Load(RepositorySources.PackageManifestPath);

    private static XElement Root() => Manifest().Root!;

    [Fact]
    public void The_provider_CLSID_is_the_same_in_all_three_places_that_declare_it()
    {
        // The single most expensive mismatch available in this project. The COM server registration
        // says which executable serves a class; the widget extension says which class to create;
        // the provider says which class it registers at runtime. Two of the three agreeing is
        // enough to install, appear in the picker, and fail on pin with nothing logged anywhere the
        // user or the developer would look.
        XElement root = Root();

        string comClassId = root.Descendants(XName.Get("Class", Com))
            .Select(e => e.Attribute("Id")!.Value)
            .Single();

        // CreateInstance is inside uap3:Properties, whose children are in no namespace: the
        // manifest declares no default namespace override there, so they inherit the foundation
        // namespace from the document element.
        string activationClassId = root.Descendants()
            .Where(e => e.Name.LocalName == "CreateInstance")
            .Select(e => e.Attribute("ClassId")!.Value)
            .Single();

        string providerSource = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "Program.cs"));

        Match declared = Regex.Match(
            providerSource,
            @"ProviderClassId\s*=\s*new\(""(?<guid>[0-9A-Fa-f-]{36})""\)");

        Assert.True(
            declared.Success,
            "Program.ProviderClassId is not declared in the expected form, so this test cannot "
                + "read the CLSID the provider actually registers. Update the pattern together "
                + "with the declaration.");

        string registeredClassId = declared.Groups["guid"].Value;

        // Compared as parsed GUIDs, not strings. Casing and brace style are irrelevant to COM and
        // a test that failed on them would be noise, but a genuinely different value must fail.
        Assert.Equal(Guid.Parse(comClassId), Guid.Parse(activationClassId));
        Assert.Equal(Guid.Parse(comClassId), Guid.Parse(registeredClassId));
    }

    [Fact]
    public void The_widget_extension_uses_the_exact_platform_extension_name()
    {
        // Fixed by the platform. Anything else is not a widget provider registration, and because
        // windows.appExtension is a general-purpose extension point, a typo here registers a
        // perfectly valid extension that nothing consumes.
        XElement extension = Root().Descendants(XName.Get("AppExtension", Uap3)).Single();

        Assert.Equal("com.microsoft.windows.widgets", extension.Attribute("Name")!.Value);
    }

    [Fact]
    public void The_com_server_points_at_the_provider_executable_and_not_the_companion()
    {
        // The provider is a COM server; the companion is an application. Pointing the ExeServer at
        // the companion would start the wrong process on activation, and the companion would show
        // its window and exit without ever registering a class object — which looks like a
        // provider crash.
        XElement exeServer = Root().Descendants(XName.Get("ExeServer", Com)).Single();

        Assert.Equal(
            @"OutlookWidget.Provider\OutlookWidget.Provider.exe",
            exeServer.Attribute("Executable")!.Value);
    }

    [Fact]
    public void The_companion_application_executable_is_unchanged()
    {
        // The provider launches the companion by application user model ID, built from the package
        // family name and this Application's Id. Changing the Id silently breaks the only action
        // offered by every signed-out and sign-in-required card.
        XElement application = Root().Descendants(XName.Get("Application", Foundation)).Single();

        Assert.Equal("App", application.Attribute("Id")!.Value);
        Assert.Equal(
            @"OutlookWidget.App\OutlookWidget.App.exe",
            application.Attribute("Executable")!.Value);
    }

    [Fact]
    public void The_widget_screenshots_are_the_size_the_picker_documentation_specifies()
    {
        // 300x304 with transparent corners. Documented, and a wrong size does not fail anything:
        // the picker accepts the image and stretches it into its 300x304 slot, so a 480x480 opaque
        // square renders as a plain block and looks like the provider failed to supply a preview.
        //
        // Read as bytes rather than with an imaging library, because the test project has no
        // graphics dependency and a PNG's IHDR is at a fixed offset.
        string assets = RepositorySources.PackageAssetsDirectory;

        string[] screenshots = [.. Root().Descendants()
            .Where(e => e.Name.LocalName == "Screenshot")
            .Select(e => e.Attribute("Path")!.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Assert.NotEmpty(screenshots);

        foreach (string reference in screenshots)
        {
            string path = Path.Combine(assets, reference[@"Assets\".Length..]);
            (int width, int height) = ReadPngSize(path);

            Assert.Equal((300, 304), (width, height));
        }
    }

    /// <summary>
    /// Reads a PNG's dimensions from its IHDR chunk.
    /// </summary>
    /// <remarks>
    /// A PNG begins with an 8-byte signature, then a 4-byte length, then "IHDR", then width and
    /// height as big-endian 32-bit integers — so both live at fixed offsets 16 and 20.
    /// </remarks>
    private static (int Width, int Height) ReadPngSize(string path)
    {
        byte[] header = new byte[24];

        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(header.Length, stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false));
        }

        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(header, 12, 4));

        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));

        return (width, height);
    }

    [Fact]
    public void Every_widget_definition_declares_an_icon_and_a_screenshot()
    {
        // Both are documented as required, and the screenshot is the one whose absence is actively
        // misleading: a definition without one does not appear in the Add Widgets picker at all,
        // which is indistinguishable from the provider failing to register.
        XElement[] definitions = [.. Root().Descendants().Where(e => e.Name.LocalName == "Definition")];

        Assert.NotEmpty(definitions);

        foreach (XElement definition in definitions)
        {
            string id = definition.Attribute("Id")!.Value;

            Assert.True(
                definition.Descendants().Any(e => e.Name.LocalName == "Icon"),
                $"Widget definition '{id}' declares no Icon, which the widget host requires for "
                    + "the attribution area.");

            Assert.True(
                definition.Descendants().Any(e => e.Name.LocalName == "Screenshot"),
                $"Widget definition '{id}' declares no Screenshot, so it will not appear in the "
                    + "Add Widgets picker.");
        }
    }

    [Fact]
    public void The_inbox_widget_declares_all_three_sizes()
    {
        // Omitting Capabilities defaults to large only. The card uses $when conditions on
        // $host.widgetSize for small and medium, so a large-only registration would leave those
        // branches unreachable and gate 5 — two instances at different sizes — unexercisable.
        XElement definition = Root().Descendants()
            .Single(e => e.Name.LocalName == "Definition"
                         && e.Attribute("Id")!.Value == "InboxWidget");

        string[] sizes = [.. definition.Descendants()
            .Where(e => e.Name.LocalName == "Size")
            .Select(e => e.Attribute("Name")!.Value)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["large", "medium", "small"], sizes);

        // Multiple instances of one definition is what gates 4 and 5 pin twice.
        Assert.Equal("true", definition.Attribute("AllowMultiple")?.Value);
    }

    [Fact]
    public void The_windows_app_sdk_framework_dependency_is_declared_and_matches_the_pinned_version()
    {
        // Without this the package installs and the provider fails to activate on a missing native
        // DLL, because the provider's output carries only the managed projections and the
        // implementation lives in the framework package.
        //
        // The values are those in the pinned Microsoft.WindowsAppSDK 2.3.1 package's own
        // WindowsAppSDK-VersionInfo.xml. If Directory.Packages.props moves to a different Windows
        // App SDK, this is the assertion that should fail — the manifest has to move with it, and
        // nothing else in the build connects the two.
        XElement dependency = Root().Descendants(XName.Get("PackageDependency", Foundation))
            .Single(e => e.Attribute("Name")!.Value.StartsWith(
                "Microsoft.WindowsAppRuntime", StringComparison.Ordinal));

        Assert.Equal("Microsoft.WindowsAppRuntime.2", dependency.Attribute("Name")!.Value);
        Assert.Equal("2.3.1.0", dependency.Attribute("MinVersion")!.Value);
        Assert.Equal(
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            dependency.Attribute("Publisher")!.Value);
    }

    [Fact]
    public void The_publisher_keeps_the_quoting_the_certificate_subject_requires()
    {
        // Package identity is name plus publisher. Windows normalizes CN=415 Group, Inc. to
        // CN="415 Group, Inc." because a comma separates elements in an X.500 name, so the quoted
        // form is the one the certificate signs. Removing the quotes still looks correct and
        // produces a different package that installs alongside the existing one instead of
        // upgrading it, losing widget pins and package-local state.
        XElement identity = Root().Element(XName.Get("Identity", Foundation))!;

        Assert.Equal("415Group.OutlookInboxWidget", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=\"415 Group, Inc.\"", identity.Attribute("Publisher")!.Value);

        // A four-part version, because MSIX requires one and a three-part value is rejected late.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", identity.Attribute("Version")!.Value);
    }

    [Fact]
    public void Every_image_the_manifest_references_exists_in_the_package_assets()
    {
        // makeappx does catch a missing asset, but only once packaging runs, and its message does
        // not say which reference was wrong. The build script performs the same check against the
        // assembled layout; this one runs on every dotnet test.
        //
        // Executable attributes are deliberately excluded: those are build outputs and do not exist
        // until publish.
        string assets = RepositorySources.PackageAssetsDirectory;
        var missing = new List<string>();

        XElement root = Root();

        IEnumerable<string> fromAttributes = root.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "Path"
                        || a.Name.LocalName.EndsWith("Logo", StringComparison.Ordinal))
            .Select(a => a.Value);

        // Properties/Logo is an element whose text is the path, not an attribute. Gathering it with
        // the attributes matched nothing and left the store logo unchecked — the build script had
        // the same bug, found by the store logo reference going uncounted.
        IEnumerable<string> fromElements = root.Descendants(XName.Get("Logo", Foundation))
            .Select(e => e.Value);

        string[] referenced = [.. fromAttributes.Concat(fromElements)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        // Asserted explicitly rather than just looped over, so a selector that quietly stops
        // matching fails here instead of passing an empty loop. The store logo appears twice in the
        // manifest — as Properties/Logo and as the provider icon — and collapses to one entry.
        //
        // Only the unqualified names appear here. The scale- and targetsize-qualified variants are
        // never named in the manifest: the resource loader finds them from resources.pri, which is
        // why Build-Package.ps1 runs makepri.
        Assert.Equal(
            [
                @"Assets\Square150x150Logo.png",
                @"Assets\Square44x44Logo.png",
                @"Assets\StoreLogo.png",
                @"Assets\WidgetIcon.png",
                @"Assets\WidgetScreenshot.png",
                @"Assets\WidgetScreenshotDark.png",
                @"Assets\WidgetScreenshotLight.png",
            ],
            referenced.Order(StringComparer.Ordinal));

        foreach (string reference in referenced)
        {
            // Every image reference in this manifest is package-relative under Assets\.
            Assert.StartsWith(@"Assets\", reference, StringComparison.Ordinal);

            string fileName = reference[@"Assets\".Length..];

            if (!File.Exists(Path.Combine(assets, fileName)))
            {
                missing.Add(reference);
            }
        }

        Assert.True(
            missing.Count == 0,
            "The manifest references images that are not in the package assets: "
                + string.Join(", ", missing)
                + ". Run scripts/New-PlaceholderAssets.ps1.");
    }

    [Fact]
    public void The_manifest_requests_no_capability_beyond_internet_and_full_trust()
    {
        // Section 3 forbids broad filesystem access, elevation, a run-full-trust service, or any
        // other restricted capability unless Phase 0 proves one is strictly required. The widget
        // registration needs none, so adding the registration must not have added a capability.
        XElement capabilities = Root().Element(XName.Get("Capabilities", Foundation))!;

        string[] declared = [.. capabilities.Elements()
            .Select(e => e.Attribute("Name")!.Value)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["internetClient", "runFullTrust"], declared);
    }

    [Fact]
    public void The_manifest_is_well_formed_and_declares_the_namespaces_it_uses()
    {
        // An undeclared prefix is a parse error rather than a subtle one, so this is really about
        // IgnorableNamespaces: a prefix present in the document but absent from that attribute
        // makes older Windows versions reject the whole package rather than skip the element.
        XElement root = Root();

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
        using (XmlReader reader = XmlReader.Create(RepositorySources.PackageManifestPath, settings))
        {
            while (reader.Read())
            {
                // Reading to the end is the well-formedness check; an exception fails the test.
            }
        }

        string ignorable = root.Attribute("IgnorableNamespaces")!.Value;

        foreach (string prefix in new[] { "uap", "uap3", "com", "rescap" })
        {
            Assert.NotNull(root.GetNamespaceOfPrefix(prefix));

            Assert.Contains(
                prefix,
                ignorable.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
        }
    }
}
