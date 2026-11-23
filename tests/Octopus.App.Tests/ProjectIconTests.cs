using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
using SkiaSharp;
using TKCode.Arc.Icons.Lucide;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>A project's square can show an icon or a picture; a picture is kept only as a small thumbnail.</summary>
public class ProjectIconTests
{
    /// <summary>Writes a <paramref name="width"/> × <paramref name="height"/> image: red left half, blue right half.</summary>
    private static string WriteImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("octopus-icon-").FullName, "source." + format.ToString().ToLowerInvariant());
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        }
        using var data = bitmap.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact, FreshApp]
    public async Task A_picture_becomes_a_small_square_thumbnail()
    {
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var source = WriteImage(3000, 1000, SKEncodedImageFormat.Jpeg);

        var name = await store.ImportAsync("p1", source);

        Assert.NotNull(name);
        var path = Path.Combine(store.Directory, name!);
        using (var codec = SKCodec.Create(path))
        {
            Assert.Equal(ProjectIconStore.ThumbnailSize, codec.Info.Width);
            Assert.Equal(ProjectIconStore.ThumbnailSize, codec.Info.Height);
        }
        Assert.True(new FileInfo(path).Length < 64 * 1024);
        // The centre square of a red | blue picture: red on the left of the middle, blue on the right.
        using (var thumbnail = SKBitmap.Decode(path))
        {
            Assert.True(thumbnail.GetPixel(20, 64).Red > 200);
            Assert.True(thumbnail.GetPixel(108, 64).Blue > 200);
        }

        var bitmap = store.Load(name);
        Assert.NotNull(bitmap);
        Assert.Same(bitmap, store.Load(name));

        store.Delete(name);
        Assert.False(File.Exists(path));
        Assert.Null(store.Load(name));
    }

    [AvaloniaFact, FreshApp]
    public async Task A_file_that_is_not_an_image_is_refused_and_names_cannot_leave_the_folder()
    {
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var text = Path.GetTempFileName();
        File.WriteAllText(text, "not an image");

        Assert.Null(await store.ImportAsync("p1", text));
        Assert.Null(await store.ImportAsync("p1", text + ".missing"));
        Assert.Null(store.Load("../projects.json"));
        store.Delete("../" + Path.GetFileName(text));
        Assert.True(File.Exists(text));
    }

    [AvaloniaFact, FreshApp]
    public async Task The_dialog_keeps_only_the_thumbnail_it_returns()
    {
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var pickers = AppHost.Get<FakePickerService>();
        var project = new Project { Name = "web", Path = "/code/web", Color = "#0EA5E9", Icon = "Rocket" };
        var form = new ProjectAppearanceView(project, pickers, store);
        Assert.Equal(new ProjectAppearanceChoice("#0EA5E9", "Rocket", null), form.Choice);

        pickers.Image = WriteImage(400, 400);
        await form.ChoosePictureAsync();
        var first = form.Image;
        await form.ChoosePictureAsync();
        var second = form.Image;
        Assert.NotNull(first);
        Assert.NotEqual(first, second);

        form.DiscardUnused(second);
        Assert.False(File.Exists(Path.Combine(store.Directory, first!)));
        Assert.True(File.Exists(Path.Combine(store.Directory, second!)));
    }

    [AvaloniaFact, FreshApp]
    public async Task The_rail_shows_the_chosen_icon_or_picture()
    {
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var project = projects.AddProject(Directory.CreateTempSubdirectory("octopus-icon-project-").FullName);
        ProjectChipViewModel Chip() => rail.Chips.Single(c => c.Id == project.Id);
        Assert.True(Chip().ShowInitial);

        // An icon.
        dialogs.Appearance = new ProjectAppearanceChoice("#10B981", "Rocket", null);
        await Chip().ChangeIconCommand.ExecuteAsync(null);
        Assert.Equal("Rocket", project.Icon);
        Assert.Equal(LucideIconKind.Rocket, Chip().IconKind);
        Assert.True(Chip().ShowIcon);

        // A picture wins over the icon.
        var picture = await store.ImportAsync(project.Id, WriteImage(300, 200));
        dialogs.Appearance = new ProjectAppearanceChoice("#10B981", "Rocket", picture);
        await Chip().ChangeIconCommand.ExecuteAsync(null);
        Assert.True(Chip().HasImage);
        Assert.False(Chip().ShowIcon);

        // Back to the initial: the old thumbnail is deleted.
        dialogs.Appearance = new ProjectAppearanceChoice("#10B981", null, null);
        await Chip().ChangeIconCommand.ExecuteAsync(null);
        Assert.True(Chip().ShowInitial);
        Assert.False(File.Exists(Path.Combine(store.Directory, picture!)));

        // Dismissing changes nothing.
        dialogs.Appearance = null;
        await Chip().ChangeIconCommand.ExecuteAsync(null);
        Assert.True(Chip().ShowInitial);
        projects.RemoveProject(project.Id);
    }

    [AvaloniaFact, FreshApp]
    public async Task A_custom_picture_clears_the_random_background_and_stays_full_opacity()
    {
        var projects = AppHost.Get<IProjectService>();
        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var rail = AppHost.Get<ProjectRailViewModel>();
        var first = projects.AddProject(Directory.CreateTempSubdirectory("octopus-pic-a-").FullName, "Alpha");
        var second = projects.AddProject(Directory.CreateTempSubdirectory("octopus-pic-b-").FullName, "Beta");
        var picture = await store.ImportAsync(first.Id, WriteImage(256, 256));
        projects.SetProjectAppearance(first.Id, first.Color, null, picture);
        var group = projects.AddGroup("Apps", [first.Id, second.Id]);
        projects.SetCurrent(second.Id);
        projects.SetExpandedGroup(null);

        var firstChip = rail.Chips.Single(c => c.Id == first.Id);
        var secondChip = rail.Chips.Single(c => c.Id == second.Id);
        Assert.True(firstChip.HasImage);
        Assert.False(firstChip.IsCurrent);
        // Custom pictures do not show the project's random color behind transparent edges and are not dimmed.
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)firstChip.Background).Color);
        Assert.Equal(1, firstChip.ChipOpacity);
        Assert.False(firstChip.ShowDimBackdrop);

        // Non-picture tiles keep the project color and show the neutral backdrop only while inactive.
        Assert.Equal(Color.Parse(second.Color!), ((ISolidColorBrush)secondChip.Background).Color);
        Assert.False(secondChip.ShowDimBackdrop);
        projects.SetCurrent(first.Id);
        Assert.True(secondChip.ShowDimBackdrop);

        // Inside a collapsed folder preview, the picture tile also uses a transparent background while the color tile keeps its color.
        var folder = rail.Entries.OfType<ProjectGroupViewModel>().Single(g => g.Id == group.Id);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)folder.Previews[0].Background).Color);
        Assert.True(folder.Previews[0].HasImage);
        Assert.Equal(Color.Parse(second.Color!), ((ISolidColorBrush)folder.Previews[1].Background).Color);

        // In the appearance dialog preview, picking a picture clears the color background too.
        var pickers = AppHost.Get<FakePickerService>();
        var form = new ProjectAppearanceView(new Project { Name = "Alpha", Path = first.Path, Color = "#10B981" }, pickers, store);
        var preview = form.FindControl<Border>("Preview")!;
        Assert.Equal(Color.Parse("#10B981"), ((ISolidColorBrush)preview.Background!).Color);
        pickers.Image = WriteImage(256, 256);
        await form.ChoosePictureAsync();
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)preview.Background!).Color);
        form.SelectIcon("Rocket");
        Assert.Equal(Color.Parse("#10B981"), ((ISolidColorBrush)preview.Background!).Color);
        form.DiscardUnused(null);
    }

    [AvaloniaFact, FreshApp]
    public async Task A_transparent_picture_renders_without_the_random_color_bleeding_through()
    {
        // A transparent PNG with a solid red square in the centre and transparent margins, placed on a project whose
        // random fallback color is bright green (#00FF00): rendering the chip on a dark surface must never show green
        // in the margins, and the centre must stay vivid red even when the project is not current.
        var path = Path.Combine(Directory.CreateTempSubdirectory("octopus-transparent-icon-").FullName, "logo.png");
        using (var src = new SKBitmap(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul)))
        using (var canvas = new SKCanvas(src))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(220, 30, 30, 255) };
            canvas.DrawRect(32, 32, 64, 64, paint);
            using var data = src.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }

        var store = (ProjectIconStore)AppHost.Get<IProjectIconStore>();
        var imported = await store.ImportAsync("p-transparent", path);
        var bitmap = store.Load(imported);
        Assert.NotNull(bitmap);

        var chip = new ProjectChipViewModel(
            new Project { Name = "Logo", Path = "/code/logo", Color = "#00FF00", Image = imported },
            isCurrent: false,
            _ => { }, _ => { }, _ => { }, _ => Task.CompletedTask,
            image: bitmap);

        var view = new ProjectChipView { DataContext = chip };
        var window = new Window
        {
            Width = 64,
            Height = 64,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
            Content = view,
        };
        window.Show();
        Frames.Pump();

        var shot = Path.Combine(Directory.CreateTempSubdirectory("octopus-chip-shot-").FullName, "chip.png");
        Frames.Save(window, shot);
        window.Close();

        using var rendered = SKBitmap.Decode(shot);
        var cx = rendered.Width / 2;
        var cy = rendered.Height / 2;
        // Centre pixel: vivid red (not dimmed by 0.72 opacity).
        var center = rendered.GetPixel(cx, cy);
        Assert.True(center.Red > 200 && center.Green < 50 && center.Blue < 50, $"center={center}");
        // Transparent margin inside the chip (just inside top-left corner of the 28px chip): dark window background, not green!
        var margin = rendered.GetPixel(cx - 10, cy - 10);
        Assert.True(margin.Green < 60, $"margin pixel leaked fallback green: {margin}");
    }

    [AvaloniaFact]
    public void The_pixel_icons_load_whole_and_their_rectangles_cover_exactly_their_pixels()
    {
        var all = Octopus.App.Common.PixelIcons.All;
        Assert.True(all.Count >= 500, $"{all.Count} pixel icons");
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct().Count());
        Assert.All(all, icon => Assert.Contains(icon.Category, Octopus.App.Common.PixelIcons.Categories));
        foreach (var category in Octopus.App.Common.PixelIcons.Categories)
        {
            Assert.Contains(all, i => i.Category == category);
        }
        foreach (var icon in all)
        {
            var covered = new bool[24, 24];
            foreach (var r in icon.Rects)
            {
                for (var y = r.Y; y < r.Y + r.Height; y++)
                {
                    for (var x = r.X; x < r.X + r.Width; x++)
                    {
                        Assert.False(covered[x, y], $"{icon.Id}: rectangles overlap at {x},{y}");
                        covered[x, y] = true;
                    }
                }
            }
            for (var y = 0; y < 24; y++)
            {
                for (var x = 0; x < 24; x++)
                {
                    Assert.True(covered[x, y] == icon[x, y], $"{icon.Id}: pixel {x},{y}");
                }
            }
            Assert.NotEmpty(icon.Rects);
        }
    }

    [AvaloniaTheory]
    [InlineData("pixel:t-rex", true)]
    [InlineData("pixel:hn-anthropic", true)]
    [InlineData("pixel:no-such-icon", false)]
    [InlineData("t-rex", false)]
    [InlineData("Rocket", false)]
    [InlineData(null, false)]
    public void Only_stored_pixel_icon_references_name_a_pixel_icon(string? stored, bool known)
    {
        var icon = Octopus.App.Common.PixelIcons.Parse(stored);
        Assert.Equal(known, icon is not null);
        if (icon is not null)
        {
            Assert.Equal(stored, Octopus.App.Common.PixelIcons.Reference(icon));
        }
    }

    [AvaloniaFact]
    public void A_square_shows_a_pixel_icon_a_line_icon_or_the_initial()
    {
        ProjectChipViewModel Chip(string? icon) =>
            new(new Project { Name = "web", Path = "/code/web", Icon = icon }, false, _ => { }, _ => { }, _ => { }, _ => Task.CompletedTask);

        var pixel = Chip("pixel:t-rex");
        Assert.True(pixel.ShowPixel);
        Assert.False(pixel.ShowIcon);
        Assert.False(pixel.ShowInitial);
        Assert.Equal("t-rex", pixel.Pixel!.Name);

        var line = Chip("Rocket");
        Assert.True(line.ShowIcon);
        Assert.False(line.ShowPixel);

        var unknown = Chip("pixel:no-such-icon");
        Assert.True(unknown.ShowInitial);
    }

    [AvaloniaFact, FreshApp]
    public void The_icon_dialog_shows_a_category_at_a_time_and_searches_them_all()
    {
        var project = new Project { Name = "web", Path = "/code/web", Color = "#0EA5E9", Icon = "pixel:t-rex" };
        var form = new ProjectAppearanceView(project, null, null);
        // It opens on the category of the chosen icon; the initial comes first.
        Assert.Equal("pixel:t-rex", form.Choice.Icon);
        Assert.Null(form.ShownIcons[0]);
        Assert.Contains("pixel:t-rex", form.ShownIcons);
        Assert.All(form.ShownIcons.Skip(1), i => Assert.Equal("fun", Octopus.App.Common.PixelIcons.Parse(i)!.Category));

        form.ShowCategory(ProjectAppearanceView.LineCategory);
        Assert.Contains("Rocket", form.ShownIcons);
        Assert.DoesNotContain("pixel:t-rex", form.ShownIcons);

        form.ShowCategory("brands");
        Assert.Contains("pixel:github", form.ShownIcons);

        // A search looks through every category, line icons included.
        form.SearchIcons("rocket");
        Assert.Contains("Rocket", form.ShownIcons);
        form.SearchIcons("rex");
        Assert.Contains("pixel:t-rex", form.ShownIcons);
        form.SearchIcons("no such icon at all");
        Assert.Equal(new string?[] { null }, form.ShownIcons);

        form.SelectIcon("pixel:snail");
        Assert.Equal(new ProjectAppearanceChoice("#0EA5E9", "pixel:snail", null), form.Choice);
        form.SelectIcon(null);
        Assert.Null(form.Choice.Icon);
    }

    [Theory]
    [InlineData("Rocket", true)]
    [InlineData("rocket", false)]
    [InlineData("NoSuchIcon", false)]
    [InlineData("12", false)]
    [InlineData(null, false)]
    public void Only_known_icon_names_are_shown(string? name, bool known)
    {
        Assert.Equal(known, ProjectIcons.Parse(name) is not null);
    }
}
