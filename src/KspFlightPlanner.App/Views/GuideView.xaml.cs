using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using KspFlightPlanner.App.Controls;

namespace KspFlightPlanner.App.Views;

/// <summary>Shows docs/MATH.md (embedded at build time) as a formatted document.</summary>
public partial class GuideView : UserControl
{
    private const string ResourceName = "KspFlightPlanner.App.MATH.md";

    public GuideView()
    {
        InitializeComponent();

        using var stream = typeof(GuideView).Assembly.GetManifestResourceStream(ResourceName);
        var markdown = stream is null ? "# Guide missing\n\nThe math guide was not embedded in this build." : new StreamReader(stream).ReadToEnd();
        var document = MarkdownRenderer.Render(markdown);

        // Follow the app theme's text colour (light or dark).
        BindingOperations.SetBinding(document, FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Viewer.Document = document;
    }
}
