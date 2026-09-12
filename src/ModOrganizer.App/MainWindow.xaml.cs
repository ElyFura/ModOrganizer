using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModOrganizer.App.ViewModels;

namespace ModOrganizer.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ViewModels.MainViewModel vm)
                vm.FocusSearchRequested += (_, _) => Dispatcher.Invoke(FocusSearchBox);
        };
    }

    private void FocusSearchBox()
    {
        if (FindName("SearchBox") is System.Windows.Controls.TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModCardViewModel card) return;
        if (DataContext is not MainViewModel vm) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            card.IsSelected = !card.IsSelected;
            vm.RefreshSelectedCount();
            e.Handled = true;
            return;
        }

        vm.SelectedMod = card;

        if (e.ClickCount == 2)
        {
            vm.OpenDetailCommand.Execute(card);
            e.Handled = true;
        }
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModCardViewModel card) return;
        if (DataContext is not MainViewModel vm) return;

        vm.SelectedMod = card;

        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Details öffnen" };
        openItem.Click += (_, _) => vm.OpenDetailCommand.Execute(card);
        menu.Items.Add(openItem);

        var explorerItem = new MenuItem { Header = "Im Explorer anzeigen" };
        explorerItem.Click += (_, _) => OpenModInExplorer(card);
        menu.Items.Add(explorerItem);

        menu.Items.Add(new Separator());

        var moveHeader = new MenuItem { Header = "In Kategorie verschieben" };
        foreach (var cat in vm.Categories)
        {
            if (cat.Id == card.Model.CategoryId) continue;
            var capturedCat = cat;
            var item = new MenuItem { Header = cat.Name };
            item.Click += (_, _) => vm.MoveSelectedToCategoryCommand.Execute(capturedCat);
            moveHeader.Items.Add(item);
        }
        if (moveHeader.Items.Count == 0)
            moveHeader.IsEnabled = false;
        menu.Items.Add(moveHeader);

        BuildTagMenu(menu, vm, card);

        fe.ContextMenu = menu;
        menu.PlacementTarget = fe;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Bulk-tagging entries. The commands act on every ticked card, falling back to the
    /// right-clicked one, so the same menu serves single and multi-select.
    /// </summary>
    private static void BuildTagMenu(ContextMenu menu, MainViewModel vm, ModCardViewModel card)
    {
        var selectedCount = vm.Mods.Count(m => m.IsSelected);
        var scope = selectedCount > 1 ? $"{selectedCount} ausgewählte Mods" : $"„{card.DisplayName}“";

        menu.Items.Add(new Separator());

        var header = new MenuItem { Header = $"Tags · {scope}", IsEnabled = false };
        menu.Items.Add(header);

        var addHeader = new MenuItem { Header = "Tag hinzufügen" };
        foreach (var tag in vm.AllTags)
        {
            var captured = tag;
            var item = new MenuItem
            {
                Header = tag.Name,
                Icon = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = TagBrushes.Solid(tag.ColorHex, tag.Name)
                }
            };
            item.Click += (_, _) => vm.AddTagToSelectionCommand.Execute(captured);
            addHeader.Items.Add(item);
        }
        if (addHeader.Items.Count > 0) addHeader.Items.Add(new Separator());

        var createItem = new MenuItem { Header = "Neues Tag anlegen…" };
        createItem.Click += (_, _) => vm.CreateAndApplyTagCommand.Execute(null);
        addHeader.Items.Add(createItem);
        menu.Items.Add(addHeader);

        // Only offer removal for tags the affected mods actually carry.
        var affected = selectedCount > 1
            ? vm.Mods.Where(m => m.IsSelected).ToList()
            : new List<ModCardViewModel> { card };
        var presentTagIds = affected
            .SelectMany(m => m.Model.Tags.Select(t => t.Id))
            .ToHashSet();

        var removeHeader = new MenuItem { Header = "Tag entfernen" };
        foreach (var tag in vm.AllTags.Where(t => presentTagIds.Contains(t.Id)))
        {
            var captured = tag;
            var item = new MenuItem { Header = tag.Name };
            item.Click += (_, _) => vm.RemoveTagFromSelectionCommand.Execute(captured);
            removeHeader.Items.Add(item);
        }
        removeHeader.IsEnabled = removeHeader.Items.Count > 0;
        menu.Items.Add(removeHeader);

        var manageItem = new MenuItem { Header = "Tags verwalten…" };
        manageItem.Click += (_, _) => vm.OpenTagManagerCommand.Execute(null);
        menu.Items.Add(manageItem);
    }

    private static void OpenModInExplorer(ModCardViewModel card)
    {
        try
        {
            if (Directory.Exists(card.Model.FolderAbsPath))
                Process.Start("explorer.exe", $"\"{card.Model.FolderAbsPath}\"");
        }
        catch { }
    }

    /// <summary>Leaves the "only missing mods" review list and shows the library again.</summary>
    private void ExitMissingView_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowMissingOnly = false;
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Services.Toast t)
        {
            t.OnClick?.Invoke();
            if (DataContext is MainViewModel vm)
                vm.Toasts?.Dismiss(t);
        }
    }

    private void FolderTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree) return;
        if (tree.SelectedItem is not FolderNodeViewModel node) return;
        if (DataContext is not MainViewModel vm) return;

        if (node.ModId is not null)
        {
            vm.OpenDetailFromNodeCommand.Execute(node);
            e.Handled = true;
        }
    }

    // --- Drag/Drop: file import onto window ---
    private static readonly string[] ImportExtensions = { ".pmp", ".ttmp2", ".zip" };

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImportableFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        var importable = paths.Where(p => ImportExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (importable.Count > 0)
        {
            vm.ImportFilesCommand.Execute(importable);
            e.Handled = true;
        }
    }

    private static bool HasImportableFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        return paths.Any(p => ImportExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
    }

    // --- Drag/Drop: image onto a single mod card ---
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImageFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModCardViewModel card) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var images = files.Where(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
        if (images.Length == 0) return;

        card.NotifyDropped(images);
        e.Handled = true;
    }

    private static bool HasImageFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.Any(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
    }
}
