using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SubtitleEditorViewModelTests
{
    [TestMethod]
    public void BuildEditedProgramChangesOnlySubtitleTextAndAccessibleText()
    {
        DisplayProgram source = CreateProgram();
        var editor = new SubtitleEditorViewModel(source);
        EditableSubtitleBlockViewModel block = editor.Pages[0].Blocks[0];

        block.Badge = "手動バッジ";
        block.PrimaryText = "手動で修正した本文";
        block.SecondaryText = "手動で修正した補足";
        DisplayProgram edited = editor.BuildEditedProgram();

        Assert.AreEqual(source.ProgramId, edited.ProgramId);
        Assert.AreEqual(source.Kind, edited.Kind);
        Assert.AreEqual(source.Priority, edited.Priority);
        Assert.AreEqual(source.Pages[0].Blocks[0].StyleToken, edited.Pages[0].Blocks[0].StyleToken);
        Assert.AreEqual("手動バッジ", edited.Pages[0].Blocks[0].Badge);
        Assert.AreEqual("手動で修正した本文", edited.Pages[0].Blocks[0].PrimaryText);
        Assert.AreEqual("手動で修正した補足", edited.Pages[0].Blocks[0].SecondaryText);
        StringAssert.Contains(edited.Pages[0].AccessibleText, "手動で修正した本文");
        Assert.AreEqual("元の本文", source.Pages[0].Blocks[0].PrimaryText);
    }

    [TestMethod]
    public void ResetRestoresEveryPageFromSourceProgram()
    {
        DisplayProgram source = CreateProgram();
        var editor = new SubtitleEditorViewModel(source);
        editor.Pages[0].Blocks[0].PrimaryText = "変更後";

        editor.Reset();

        Assert.AreEqual("元の本文", editor.Pages[0].Blocks[0].PrimaryText);
        Assert.AreEqual("ページ 1", editor.SelectedPage?.Label);
    }

    [TestMethod]
    public void ResetAfterReopeningRestoresOfficialSourceInsteadOfPreviousManualEdit()
    {
        DisplayProgram source = CreateProgram();
        DisplayPage editedPage = source.Pages[0] with
        {
            Blocks = [source.Pages[0].Blocks[0] with { PrimaryText = "前回の手動編集" }],
        };
        DisplayProgram displayed = source with { Pages = [editedPage] };
        var editor = new SubtitleEditorViewModel(source, displayed);
        Assert.AreEqual("前回の手動編集", editor.Pages[0].Blocks[0].PrimaryText);

        editor.Reset();

        Assert.AreEqual("元の本文", editor.Pages[0].Blocks[0].PrimaryText);
    }

    private static DisplayProgram CreateProgram()
    {
        DateTimeOffset now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var block = new DisplayBlock(
            "震度4",
            "元の本文",
            "元の補足",
            DisplayStyleTokens.Intensity);
        var page = new DisplayPage(0, [block], "元の本文 元の補足", null);
        return new DisplayProgram(
            "manual-edit-test",
            EventId.Create("event-1"),
            EventKind.Quake,
            SourceMode.Production,
            now,
            OverlayPriority.Quake,
            [page],
            now,
            EndPolicy.AutoHide,
            string.Empty);
    }
}
