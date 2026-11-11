using System.ComponentModel;

namespace Persocord.Views;

/// <summary>
/// A choice in a list that also says what picking it would change
/// ("+120 MB VRAM · +4% encoder"). The difference shows in the open list
/// only; the closed box shows just the choice.
/// </summary>
public sealed class CostOption : INotifyPropertyChanged
{
    public CostOption(string label) { _label = label; }

    private string _label;
    private string _delta = "";

    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value; Changed(nameof(Label)); } }
    }

    public string Delta
    {
        get => _delta;
        set { if (_delta != value) { _delta = value; Changed(nameof(Delta)); } }
    }

    public override string ToString() => _label;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
