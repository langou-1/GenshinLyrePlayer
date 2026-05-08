using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GenshinLyrePlayer.Views;

public partial class LetterScoreDialog : Window
{
    /// <summary>用户点击「确定」后的文本内容；为 null 表示用户取消了。</summary>
    public string? ResultText { get; private set; }

    public LetterScoreDialog()
    {
        InitializeComponent();
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        ResultText = null;
        Close();
    }
}
