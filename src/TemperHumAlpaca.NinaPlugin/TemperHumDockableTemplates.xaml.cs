using System.ComponentModel.Composition;
using System.Windows;

namespace TemperHumAlpaca.NinaPlugin;

[Export(typeof(ResourceDictionary))]
public partial class TemperHumDockableTemplates : ResourceDictionary
{
    public TemperHumDockableTemplates() => InitializeComponent();
}
