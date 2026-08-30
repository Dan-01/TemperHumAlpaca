using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;

namespace TemperHumAlpaca.NinaPlugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options() => InitializeComponent();

    private void TelegramBotToken_LostKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox ||
            passwordBox.DataContext is not TemperHumPlugin plugin ||
            string.IsNullOrWhiteSpace(passwordBox.Password))
        {
            return;
        }

        plugin.SetTelegramBotToken(passwordBox.Password);
        passwordBox.Clear();
    }
}
