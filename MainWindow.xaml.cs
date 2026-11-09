using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using IconSwapper.Models;
using IconSwapper.Services;
using Microsoft.Win32;

namespace IconSwapper;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SearchHit> _hits = [];
    private readonly ObservableCollection<ExtensionChoice> _choices = [];
    private readonly ObservableCollection<LibraryItem> _library = [];
    private AssociationReader? _reader;
    private BitmapSource? _source;
    private string? _imagePath;
    private List<string> _baseExtensions = [];
    private string? _contextExe;
    private bool _fromApp;
    private bool _refreshingHits;
    private bool _working;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _hits;
        ChoiceList.ItemsSource = _choices;
        LibraryList.ItemsSource = _library;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("Reading file types…");
        try
        {
            _reader = await Task.Run(AssociationReader.Load);
            RefreshLibrary();
            if (!string.IsNullOrWhiteSpace(IconApplier.LoadWarning))
                SetStatus(IconApplier.LoadWarning);
            else
                SetStatus("Search for an extension or an app.");
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                RunSearch();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RunSearch();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunSearch();
    }

    private void RunSearch()
    {
        if (_reader == null)
            return;
        var query = SearchBox.Text.Trim();
        _refreshingHits = true;
        _hits.Clear();
        if (query.Length > 0)
        {
            foreach (var result in _reader.Search(query))
            {
                var hit = new SearchHit
                {
                    Title = result.Title,
                    Subtitle = result.Subtitle,
                    IconSpec = result.IconSpec,
                    Extensions = result.Extensions,
                    ExePath = result.ExePath,
                    IsApp = result.IsApp,
                    Icon = IconLoader.LoadSpec(result.IconSpec, 32),
                };
                _hits.Add(hit);
            }
        }

        _refreshingHits = false;
        EmptyResults.Visibility = _hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingHits || ResultsList.SelectedItem is not SearchHit hit)
            return;
        LoadExtensions(hit.Extensions, hit.IsApp ? hit.ExePath : null, hit.IsApp, keepChecks: false);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string name)
            return;
        var preset = Presets.All.FirstOrDefault(item => item.Name == name);
        if (preset == null)
            return;
        if (_reader == null)
        {
            SetStatus("Still reading file types…");
            return;
        }

        LoadExtensions(preset.Extensions, contextExe: null, fromApp: false, keepChecks: false);
        HeaderTitle.Text = preset.Name;
    }

    private void AddExtension_Click(object sender, RoutedEventArgs e) => AddExtension();

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddExtension();
    }

    private void AddExtension()
    {
        if (_reader == null)
        {
            SetStatus("Still reading file types…");
            return;
        }

        var extension = IconPaths.NormalizeExtension(AddBox.Text);
        if (extension == null)
        {
            SetStatus("Enter an extension such as .docx.");
            return;
        }

        var next = _baseExtensions.Concat([extension]).ToList();
        LoadExtensions(next, _contextExe, _fromApp, keepChecks: true);
        AddBox.Clear();
    }

    private void LoadExtensions(IEnumerable<string> extensions, string? contextExe, bool fromApp, bool keepChecks)
    {
        if (_reader == null)
            return;

        var previous = _choices.ToDictionary(choice => choice.Extension, choice => choice.IsChecked, StringComparer.OrdinalIgnoreCase);
        _baseExtensions = extensions
            .Select(IconPaths.NormalizeExtension)
            .Where(item => item != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _contextExe = string.IsNullOrWhiteSpace(contextExe) ? null : contextExe;
        _fromApp = fromApp;

        _choices.Clear();
        foreach (var option in _reader.BuildChoices(_baseExtensions))
        {
            var choice = new ExtensionChoice
            {
                Detail = option.Detail,
                Note = option.Note,
                IsChecked = !keepChecks || !previous.TryGetValue(option.Detail.Extension, out var was) || was,
            };
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ExtensionChoice.IsChecked))
                    UpdateEditor();
            };
            _choices.Add(choice);
        }

        if (!keepChecks)
            ShortcutCheck.IsChecked = ShortcutTargets().Count > 0;
        UpdateEditor();
    }

    private void UpdateEditor()
    {
        var selected = _choices.Where(choice => choice.IsChecked).ToList();
        if (_choices.Count == 0)
        {
            HeaderTitle.Text = "Choose a file type";
            HeaderDetail.Text = "Search for an extension or an app, or pick a group above.";
            CurrentIcon.Source = null;
            CurrentCaption.Text = "Current";
        }
        else if (selected.Count == 1)
        {
            var detail = selected[0].Detail;
            HeaderTitle.Text = detail.Extension;
            HeaderDetail.Text = Describe(detail);
            ShowCurrent(detail.IconSpec);
        }
        else if (selected.Count > 1)
        {
            HeaderTitle.Text = selected.Count + " file types";
            HeaderDetail.Text = string.Join("  ", selected.Select(choice => choice.Extension));
            ShowCurrent(selected[0].Detail.IconSpec);
        }
        else
        {
            HeaderTitle.Text = _choices.Count + " file types";
            HeaderDetail.Text = "None of them are selected.";
            CurrentIcon.Source = null;
            CurrentCaption.Text = "Current";
        }

        UpdateWarning();
        UpdateShortcutLabel();
        UpdateButtons();
    }

    private static string Describe(ExtensionDetail detail)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.TypeName))
            parts.Add(detail.TypeName);
        if (!string.IsNullOrWhiteSpace(detail.HandlerName))
            parts.Add("Opens with " + detail.HandlerName);
        else
            parts.Add("No open command");
        return string.Join(" · ", parts);
    }

    private void ShowCurrent(string? spec)
    {
        CurrentIcon.Source = IconLoader.LoadSpec(spec, 96);
        CurrentCaption.Text = CurrentIcon.Source == null ? "No icon" : "Current";
    }

    private void UpdateWarning()
    {
        if (_reader == null)
        {
            WarningText.Text = "";
            return;
        }

        var selected = _choices.Where(choice => choice.IsChecked).Select(choice => choice.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        foreach (var choice in _choices.Where(choice => choice.IsChecked))
        {
            if (choice.Detail.EffectiveProgId == null)
                continue;
            var users = _reader.ExtensionsUsing(choice.Detail.EffectiveProgId);
            var missing = users.Where(user => !selected.Contains(user)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0)
                continue;
            notes.Add(choice.Extension + " shares its icon with " + string.Join(", ", missing) + ". Leave those checked, or the shared icon stays as it is.");
        }

        WarningText.Text = string.Join(" ", notes.Distinct());
    }

    private void UpdateShortcutLabel()
    {
        var targets = ShortcutTargets();
        if (targets.Count == 0)
        {
            var names = _choices
                .Where(choice => choice.IsChecked && !string.IsNullOrWhiteSpace(choice.Detail.HandlerName))
                .Select(choice => choice.Detail.HandlerName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ShortcutCheck.IsEnabled = false;
            ShortcutCheck.Content = names.Count > 1
                ? "Several apps open these files. Search for one app to update its shortcuts."
                : "No shortcut found for these file types.";
            return;
        }

        ShortcutCheck.IsEnabled = true;
        var files = targets.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ShortcutCheck.Content = files.Count == 1
            ? "Also update " + files[0] + " shortcuts"
            : "Also update shortcuts for " + string.Join(", ", files);
    }

    private List<string> ShortcutTargets()
    {
        if (_fromApp && !string.IsNullOrWhiteSpace(_contextExe))
            return [_contextExe];

        return _choices
            .Where(choice => choice.IsChecked && !string.IsNullOrWhiteSpace(choice.Detail.HandlerExe))
            .Select(choice => choice.Detail.HandlerExe!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateButtons()
    {
        var hasType = _choices.Any(choice => choice.IsChecked);
        var hasShortcut = ShortcutCheck.IsChecked == true && ShortcutCheck.IsEnabled && ShortcutTargets().Count > 0;
        ApplyButton.IsEnabled = !_working && _imagePath != null && (hasType || hasShortcut);
        RestoreButton.IsEnabled = !_working && _choices.Any(choice =>
            choice.IsChecked && IconApplier.HasExtension(choice.Extension));
    }

    private void ShortcutCheck_Changed(object sender, RoutedEventArgs e) => UpdateButtons();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.ico",
            Title = "Choose an icon",
        };
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (Directory.Exists(pictures))
            dialog.InitialDirectory = pictures;
        if (dialog.ShowDialog(this) == true)
            ChooseImage(dialog.FileName);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        var image = files.FirstOrDefault(IconConverter.IsAllowed) ?? files[0];
        ChooseImage(image);
        e.Handled = true;
    }

    private void ChooseImage(string path)
    {
        try
        {
            _source = IconConverter.Decode(path);
            _imagePath = path;
            DropLabel.Text = Path.GetFileName(path);
            Preview16.Source = IconConverter.Render(_source, 16);
            Preview32.Source = IconConverter.Render(_source, 32);
            Preview48.Source = IconConverter.Render(_source, 48);
            Preview256.Source = IconConverter.Render(_source, 256);
            SetStatus("Ready to use " + Path.GetFileName(path) + ".");
        }
        catch (Exception ex)
        {
            _source = null;
            _imagePath = null;
            Preview16.Source = null;
            Preview32.Source = null;
            Preview48.Source = null;
            Preview256.Source = null;
            DropLabel.Text = "Drop a PNG, JPG, or ICO here";
            SetStatus(ex.Message);
        }

        UpdateButtons();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_reader == null || _imagePath == null || _working)
            return;

        var selected = _choices.Where(choice => choice.IsChecked).Select(choice => choice.Detail).ToList();
        var shortcuts = ShortcutCheck.IsChecked == true && ShortcutCheck.IsEnabled;
        var targets = shortcuts ? ShortcutTargets() : [];
        if (selected.Count == 0 && targets.Count == 0)
        {
            SetStatus("Choose a file type, or turn on shortcut update.");
            return;
        }

        _working = true;
        UpdateButtons();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var ico = IconConverter.Save(IconConverter.ToIco(_imagePath));
            var report = IconApplier.Apply(new ApplyRequest
            {
                Extensions = selected,
                ExtensionsUsing = _reader.ExtensionsUsing,
                IcoPath = ico,
                UpdateShortcuts = shortcuts,
                ShortcutExes = targets,
            });
            ShellRefresh.Notify();
            RefreshLibrary();
            LoadExtensions(_baseExtensions, _contextExe, _fromApp, keepChecks: true);
            SetStatus(report.Message);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _working = false;
            UpdateButtons();
        }
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var extensions = _choices
            .Where(choice => choice.IsChecked && IconApplier.HasExtension(choice.Extension))
            .Select(choice => choice.Extension)
            .ToList();
        if (extensions.Count == 0)
            return;
        RestoreExtensions(extensions);
    }

    private void LibraryRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.DataContext is not LibraryItem item)
            return;
        if (item.IsShortcut)
        {
            try
            {
                var message = IconApplier.RestoreShortcuts(item.Id);
                ShellRefresh.Notify();
                RefreshLibrary();
                SetStatus(message);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
            return;
        }

        RestoreExtensions([item.Id]);
    }

    private void RestoreExtensions(IReadOnlyList<string> extensions)
    {
        try
        {
            var messages = extensions.Select(IconApplier.RestoreExtension).ToList();
            ShellRefresh.Notify();
            RefreshLibrary();
            if (_reader != null && _baseExtensions.Count > 0)
                LoadExtensions(_baseExtensions, _contextExe, _fromApp, keepChecks: true);
            SetStatus(string.Join(" ", messages));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Restarting Explorer…");
        try
        {
            ShellRefresh.RestartExplorer();
            SetStatus("Explorer restarted. Your new icons should show up now.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void RefreshLibrary()
    {
        _library.Clear();
        foreach (var entry in IconApplier.List())
        {
            _library.Add(new LibraryItem
            {
                Id = entry.Id,
                Title = entry.Title,
                Subtitle = entry.Subtitle,
                IconSpec = entry.IconSpec,
                IsShortcut = entry.IsShortcut,
                Icon = IconLoader.LoadSpec(entry.IconSpec, 32),
            });
        }

        EmptyLibrary.Visibility = _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
