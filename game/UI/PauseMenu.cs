using System;
using Godot;

namespace DeadRecon.UI;

/// <summary>
/// Esc: pause the game and show settings. Resume, new operation, difficulty, mouse sensitivity, invert Y,
/// debug info, quit. Changes save straight away (Settings).
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    /// <summary>Raised when the debug-info checkbox changes, so the scene can show/hide its debug text.</summary>
    public event Action? Changed;
    public bool Open => _root.Visible;
    public DeadRecon.Player.Walker? Walker;

    private Control _root = null!;
    private Label _difficultyNote = null!;

    public override void _Ready()
    {
        Layer = 10;
        ProcessMode = ProcessModeEnum.Always; // keeps working while the game is paused

        _root = new ColorRect { Color = new Color(.03f, .05f, .03f, .78f), Visible = false };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        box.AddThemeConstantOverride("separation", 12);
        var centre = new CenterContainer();
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        centre.AddChild(box);
        _root.AddChild(centre);

        var title = new Label { Text = "PAUSED", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 36);
        box.AddChild(title);

        Button(box, "Resume", Close);
        Button(box, "New operation", () => { Close(); GetTree().ReloadCurrentScene(); });

        box.AddChild(new HSeparator());
        var diff = new OptionButton();
        foreach (var l in Enum.GetValues<Settings.Level>()) diff.AddItem(l.ToString());
        diff.Selected = (int)Settings.Difficulty;
        diff.ItemSelected += i => { Settings.Difficulty = (Settings.Level)(int)i; Settings.Save(); UpdateNote(); };
        Row(box, "Difficulty", diff);
        _difficultyNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, .7f) };
        box.AddChild(_difficultyNote);
        UpdateNote();

        var sens = new HSlider { MinValue = .2, MaxValue = 3, Step = .05, Value = Settings.MouseSensitivity, CustomMinimumSize = new Vector2(220, 0) };
        var sensLabel = new Label { Text = $"{Settings.MouseSensitivity:0.00}x" };
        sens.ValueChanged += v => { Settings.MouseSensitivity = (float)v; sensLabel.Text = $"{v:0.00}x"; Settings.Save(); };
        var sensRow = new HBoxContainer();
        sensRow.AddChild(sens);
        sensRow.AddChild(sensLabel);
        Row(box, "Mouse sensitivity", sensRow);

        Check(box, "Invert mouse Y", Settings.InvertY, v => Settings.InvertY = v);
        Check(box, "Show debug info (F3)", Settings.ShowDebug, v => Settings.ShowDebug = v);

        box.AddChild(new HSeparator());
        Button(box, "Quit to desktop", () => GetTree().Quit());
        var help = new Label
        {
            Text = "WASD move · Shift run · Ctrl crouch · Space jump · LMB fire · RMB scope · R reload · E (hold) take · N weather · T ground look",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, .55f),
        };
        box.AddChild(help);
    }

    private void UpdateNote() => _difficultyNote.Text = Settings.Describe(Settings.Difficulty);

    private static void Button(Container parent, string text, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, 40) };
        b.Pressed += pressed;
        parent.AddChild(b);
    }

    private static void Row(Container parent, string label, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(170, 0) });
        row.AddChild(control);
        parent.AddChild(row);
    }

    private void Check(Container parent, string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Text = text, ButtonPressed = value };
        c.Toggled += v => { set(v); Settings.Save(); Changed?.Invoke(); };
        parent.AddChild(c);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            if (Open) Close(); else Show();
            GetViewport().SetInputAsHandled();
        }
    }

    public new void Show()
    {
        _root.Visible = true;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Close()
    {
        _root.Visible = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (Walker != null) Walker.FireLockedUntilRelease = true; // the click on Resume must not fire
    }
}
