using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rbx2Source
{
    // Small message boxes: a message with OK, or a question with Yes and No.
    public static class Dialogs
    {
        public static Task Show(Window owner, string message, string title = "RoPorter") => Ask(owner, message, title, false);

        // Returns whether Yes was chosen.
        public static async Task<bool> Ask(Window owner, string message, string title = "RoPorter", bool question = true)
        {
            var dialog = new Window
            {
                Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, MaxWidth = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = owner.Icon
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var yes = new Button { Content = question ? "Yes" : "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
            yes.Click += (sender, args) => dialog.Close(true);
            buttons.Children.Add(yes);
            if (question)
            {
                var no = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
                no.Click += (sender, args) => dialog.Close(false);
                buttons.Children.Add(no);
            }
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 16,
                Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons }
            };
            return await dialog.ShowDialog<bool>(owner);
        }
    }
}
