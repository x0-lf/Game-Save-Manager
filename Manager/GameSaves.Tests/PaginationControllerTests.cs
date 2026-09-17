using GameSaves.App.Common;
using GameSaves.App.Models;
using GameSaves.App.ViewModels;
using GameSaves.Core.Save;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameSaves.Tests;

public sealed class PaginationControllerTests
{
    [Fact]
    public void DefaultState_HasExpectedDefaults()
    {
        var controller = new PaginationController<string>();

        Assert.Empty(controller.CurrentPageItems);
        Assert.Equal(1, controller.CurrentPage);
        Assert.Equal(20, controller.PageSize);
        Assert.Equal("20", controller.SelectedPageSizeOption);
        Assert.Equal(0, controller.TotalItemCount);
        Assert.Equal(1, controller.TotalPages);
        Assert.False(controller.HasPreviousPage);
        Assert.False(controller.HasNextPage);
        Assert.False(controller.IsCustomPageSize);
        Assert.Equal("Showing 0 of 0 items", controller.PageSummaryText);
        Assert.Equal("Page 1 of 1", controller.PageNumberText);
        Assert.False(controller.FirstPageCommand.CanExecute(null));
        Assert.False(controller.PreviousPageCommand.CanExecute(null));
        Assert.False(controller.NextPageCommand.CanExecute(null));
        Assert.False(controller.LastPageCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    public void AllPresetPageSizes_AreSupportedAndCalculatePagesCorrectly(int presetSize)
    {
        var controller = new PaginationController<int>();
        var items = Enumerable.Range(1, 150).ToList();

        controller.SelectedPageSizeOption = presetSize.ToString();
        controller.SetSource(items);

        Assert.Equal(presetSize, controller.PageSize);
        Assert.Equal(presetSize.ToString(), controller.SelectedPageSizeOption);
        Assert.False(controller.IsCustomPageSize);
        Assert.Equal(150, controller.TotalItemCount);

        int expectedPages = (int)Math.Ceiling(150.0 / presetSize);
        Assert.Equal(expectedPages, controller.TotalPages);

        // Verify page 1 items
        int expectedPage1Count = Math.Min(presetSize, 150);
        Assert.Equal(expectedPage1Count, controller.CurrentPageItems.Count);
        Assert.Equal(1, controller.CurrentPageItems[0]);
        Assert.Equal(expectedPage1Count, controller.CurrentPageItems[^1]);
    }

    [Fact]
    public void CustomPageSize_AllowsArbitraryPositiveInteger()
    {
        var controller = new PaginationController<int>();
        controller.SetSource(Enumerable.Range(1, 100));

        // Switch to custom
        controller.SelectedPageSizeOption = PaginationController<int>.CustomPageSizeOption;
        Assert.True(controller.IsCustomPageSize);

        controller.CustomPageSizeText = "37";
        Assert.Equal(37, controller.PageSize);
        Assert.Equal(37, controller.CustomPageSize);
        Assert.Equal(3, controller.TotalPages); // 37 + 37 + 26 = 100
        Assert.Equal(37, controller.CurrentPageItems.Count);

        controller.NextPage();
        Assert.Equal(2, controller.CurrentPage);
        Assert.Equal(37, controller.CurrentPageItems.Count);
        Assert.Equal(38, controller.CurrentPageItems[0]);

        controller.NextPage();
        Assert.Equal(3, controller.CurrentPage);
        Assert.Equal(26, controller.CurrentPageItems.Count);
        Assert.Equal(75, controller.CurrentPageItems[0]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("invalid")]
    [InlineData("")]
    public void CustomPageSize_RejectsInvalidInputWithoutCorruptingState(string invalidInput)
    {
        var controller = new PaginationController<int>();
        controller.SetSource(Enumerable.Range(1, 50));
        int initialPageSize = controller.PageSize;

        controller.SelectedPageSizeOption = PaginationController<int>.CustomPageSizeOption;
        controller.CustomPageSizeText = invalidInput;

        Assert.Equal(initialPageSize, controller.PageSize);
        Assert.True(controller.PageSize > 0);
    }

    [Fact]
    public void SetPageSize_SwitchesToCustomWhenNotPreset()
    {
        var controller = new PaginationController<int>();
        controller.SetPageSize(42);

        Assert.Equal(42, controller.PageSize);
        Assert.Equal(PaginationController<int>.CustomPageSizeOption, controller.SelectedPageSizeOption);
        Assert.True(controller.IsCustomPageSize);
        Assert.Equal(42, controller.CustomPageSize);
    }

    [Fact]
    public void SetPageSize_ThrowsOnNonPositive()
    {
        var controller = new PaginationController<int>();
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetPageSize(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetPageSize(-10));
    }

    [Fact]
    public void BoundsClamping_ClampsOutOfBoundsNavigation()
    {
        var controller = new PaginationController<int>();
        controller.SetPageSize(10);
        controller.SetSource(Enumerable.Range(1, 25)); // 3 pages

        Assert.Equal(3, controller.TotalPages);

        controller.GoToPage(100);
        Assert.Equal(3, controller.CurrentPage);

        controller.GoToPage(-5);
        Assert.Equal(1, controller.CurrentPage);

        controller.CurrentPage = 999;
        Assert.Equal(3, controller.CurrentPage);

        controller.CurrentPage = 0;
        Assert.Equal(1, controller.CurrentPage);
    }

    [Fact]
    public void NavigationCommands_ExecuteAndNotifyCanExecute()
    {
        var controller = new PaginationController<int>();
        controller.SetPageSize(10);
        controller.SetSource(Enumerable.Range(1, 35)); // 4 pages: 10, 10, 10, 5

        Assert.Equal(1, controller.CurrentPage);
        Assert.False(controller.FirstPageCommand.CanExecute(null));
        Assert.False(controller.PreviousPageCommand.CanExecute(null));
        Assert.True(controller.NextPageCommand.CanExecute(null));
        Assert.True(controller.LastPageCommand.CanExecute(null));

        controller.NextPageCommand.Execute(null);
        Assert.Equal(2, controller.CurrentPage);
        Assert.True(controller.FirstPageCommand.CanExecute(null));
        Assert.True(controller.PreviousPageCommand.CanExecute(null));
        Assert.True(controller.NextPageCommand.CanExecute(null));
        Assert.True(controller.LastPageCommand.CanExecute(null));

        controller.LastPageCommand.Execute(null);
        Assert.Equal(4, controller.CurrentPage);
        Assert.True(controller.FirstPageCommand.CanExecute(null));
        Assert.True(controller.PreviousPageCommand.CanExecute(null));
        Assert.False(controller.NextPageCommand.CanExecute(null));
        Assert.False(controller.LastPageCommand.CanExecute(null));

        controller.PreviousPageCommand.Execute(null);
        Assert.Equal(3, controller.CurrentPage);

        controller.FirstPageCommand.Execute(null);
        Assert.Equal(1, controller.CurrentPage);
    }

    [Fact]
    public void EdgeCases_ZeroItems_OneItem_PageSizeGreaterThanItems()
    {
        var controller = new PaginationController<string>
        {
            ItemName = "game",
            PluralItemName = "games"
        };

        // 0 items
        controller.SetSource(Array.Empty<string>());
        Assert.Equal(0, controller.TotalItemCount);
        Assert.Equal(1, controller.TotalPages);
        Assert.Equal(1, controller.CurrentPage);
        Assert.Equal("Showing 0 of 0 games", controller.PageSummaryText);
        Assert.Equal("Page 1 of 1", controller.PageNumberText);

        // 1 item
        controller.SetSource(new[] { "Portal" });
        Assert.Equal(1, controller.TotalItemCount);
        Assert.Equal(1, controller.TotalPages);
        Assert.Equal(1, controller.CurrentPage);
        Assert.Equal("Showing 1 of 1 game", controller.PageSummaryText);
        Assert.Single(controller.CurrentPageItems);

        // Page size > total items (e.g. 5 items with PageSize 20)
        controller.SetSource(new[] { "A", "B", "C", "D", "E" });
        Assert.Equal(5, controller.TotalItemCount);
        Assert.Equal(1, controller.TotalPages);
        Assert.Equal("Showing 1–5 of 5 games", controller.PageSummaryText);
    }

    [Fact]
    public void PageSummaryText_FormatsCorrectlyAcrossPages()
    {
        var controller = new PaginationController<int>
        {
            ItemName = "game",
            PluralItemName = "games"
        };

        controller.SetPageSize(20);
        controller.SetSource(Enumerable.Range(1, 342)); // 18 pages

        Assert.Equal(18, controller.TotalPages);
        Assert.Equal("Showing 1–20 of 342 games", controller.PageSummaryText);
        Assert.Equal("Page 1 of 18", controller.PageNumberText);

        controller.GoToPage(18);
        // Page 18: items 341..342 (2 items)
        Assert.Equal("Showing 341–342 of 342 games", controller.PageSummaryText);
        Assert.Equal("Page 18 of 18", controller.PageNumberText);
    }

    [Fact]
    public void FilteringAndSorting_ApplyPriorToPagination()
    {
        var controller = new PaginationController<int>();
        controller.SetPageSize(5);
        controller.SetSource(Enumerable.Range(1, 30)); // 1..30

        // Filter only even numbers: 2, 4, 6, ... 30 (15 items)
        controller.ApplyFilter(x => x % 2 == 0);
        Assert.Equal(15, controller.TotalItemCount);
        Assert.Equal(3, controller.TotalPages);
        Assert.Equal(1, controller.CurrentPage);
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, controller.CurrentPageItems);

        // Sort descending: 30, 28, 26, ...
        controller.ApplySort(items => items.OrderByDescending(x => x));
        Assert.Equal(15, controller.TotalItemCount);
        Assert.Equal(new[] { 30, 28, 26, 24, 22 }, controller.CurrentPageItems);

        // Advance to page 2
        controller.NextPage();
        Assert.Equal(2, controller.CurrentPage);
        Assert.Equal(new[] { 20, 18, 16, 14, 12 }, controller.CurrentPageItems);

        // Clear filter: restores 30 items
        controller.ApplyFilter(null, resetToFirstPage: true);
        Assert.Equal(30, controller.TotalItemCount);
        Assert.Equal(6, controller.TotalPages);
        Assert.Equal(1, controller.CurrentPage);
        // Retains descending sort: 30, 29, 28, 27, 26
        Assert.Equal(new[] { 30, 29, 28, 27, 26 }, controller.CurrentPageItems);
    }

    [Fact]
    public void SourceChange_ClampsCurrentPageIfNewTotalPagesIsSmaller()
    {
        var controller = new PaginationController<int>();
        controller.SetPageSize(10);
        controller.SetSource(Enumerable.Range(1, 50)); // 5 pages

        controller.GoToPage(5);
        Assert.Equal(5, controller.CurrentPage);

        // New source with only 20 items (2 pages)
        controller.SetSource(Enumerable.Range(1, 20));
        Assert.Equal(2, controller.TotalPages);
        Assert.Equal(2, controller.CurrentPage); // Clamped from 5 to 2
    }

    [Fact]
    public void InstalledGamesViewModel_PreservesSelectionAcrossPageSwitches()
    {
        var statusList = new List<InstalledGameSaveStatus>();
        for (int i = 1; i <= 10; i++)
        {
            statusList.Add(CreateStatus((uint)i, $"Game {i:D2}"));
        }

        var mockService = new FakeStatusService(statusList);
        var layoutService = SyncProviderSelectionTests.NewWorkspaceLayout();
        var vm = new InstalledGamesViewModel(mockService, layoutService);

        vm.Pagination.SetPageSize(3); // 10 games, 3 per page -> 4 pages

        // Trigger load
        vm.RefreshCommand.Execute(null);

        Assert.Equal(10, vm.Games.Count);
        Assert.Equal(10, vm.Pagination.TotalItemCount);
        Assert.Equal(4, vm.Pagination.TotalPages);
        Assert.Equal(3, vm.Pagination.CurrentPageItems.Count);

        // Select game 2 (on page 1)
        InstalledGameRowViewModel game2 = vm.Games[1];
        vm.SelectedGame = game2;
        Assert.Same(game2, vm.SelectedGame);

        // Switch to page 2 (contains games 4, 5, 6)
        vm.Pagination.NextPage();
        Assert.Equal(2, vm.Pagination.CurrentPage);
        Assert.DoesNotContain(game2, vm.Pagination.CurrentPageItems);

        // Invariant: Selection state is preserved across page switches
        Assert.Same(game2, vm.SelectedGame);

        // Switch to page 3 and page 4
        vm.Pagination.NextPage();
        Assert.Same(game2, vm.SelectedGame);

        vm.Pagination.NextPage();
        Assert.Same(game2, vm.SelectedGame);

        // Return to page 1
        vm.Pagination.FirstPage();
        Assert.Equal(1, vm.Pagination.CurrentPage);
        Assert.Contains(game2, vm.Pagination.CurrentPageItems);
        Assert.Same(game2, vm.SelectedGame);

        // Select a different game on page 2
        vm.Pagination.GoToPage(2);
        InstalledGameRowViewModel game5 = vm.Games[4];
        vm.SelectedGame = game5;
        Assert.Same(game5, vm.SelectedGame);

        // Move back to page 1: game 5 is preserved
        vm.Pagination.GoToPage(1);
        Assert.Same(game5, vm.SelectedGame);
    }

    [Fact]
    public void InstalledGamesViewModel_SortBy_SortsEntireCollectionPriorToPagination()
    {
        var statusList = new List<InstalledGameSaveStatus>
        {
            CreateStatus(1, "Zelda"),
            CreateStatus(2, "Apex"),
            CreateStatus(3, "Mario"),
            CreateStatus(4, "Cyberpunk"),
            CreateStatus(5, "Doom"),
        };

        var mockService = new FakeStatusService(statusList);
        var layoutService = SyncProviderSelectionTests.NewWorkspaceLayout();
        var vm = new InstalledGamesViewModel(mockService, layoutService);
        vm.Pagination.SetPageSize(2);

        vm.RefreshCommand.Execute(null);
        Assert.Equal(3, vm.Pagination.TotalPages);

        // Sort ascending by GameName: Apex, Cyberpunk, Doom, Mario, Zelda
        vm.SortBy("GameName", true);
        Assert.Equal(2, vm.Pagination.CurrentPageItems.Count);
        Assert.Equal("Apex", vm.Pagination.CurrentPageItems[0].GameName);
        Assert.Equal("Cyberpunk", vm.Pagination.CurrentPageItems[1].GameName);

        // Page 2: Doom, Mario
        vm.Pagination.NextPage();
        Assert.Equal("Doom", vm.Pagination.CurrentPageItems[0].GameName);
        Assert.Equal("Mario", vm.Pagination.CurrentPageItems[1].GameName);

        // Sort descending: Zelda, Mario, Doom, Cyberpunk, Apex
        vm.SortBy("GameName", false);
        vm.Pagination.FirstPage();
        Assert.Equal("Zelda", vm.Pagination.CurrentPageItems[0].GameName);
        Assert.Equal("Mario", vm.Pagination.CurrentPageItems[1].GameName);
    }

    private static InstalledGameSaveStatus CreateStatus(uint appId, string name)
    {
        return new InstalledGameSaveStatus(
            Game: new GameSaves.Core.Steam.SteamGame(
                appId.ToString(),
                name,
                $"C:\\Games\\{name}",
                "C:\\Steam",
                "",
                "",
                true,
                GameSaves.Core.Steam.SteamDiscoveryConfidence.High),
            Status: GameSaveStatusKind.Ready,
            StatusText: "Ready",
            ApprovedMappings: 1,
            PendingMappings: 0,
            NeedsFixMappings: 0,
            SavePathExists: true,
            FileCount: 1,
            TotalBytes: 100,
            VerificationResults: Array.Empty<SavePathVerificationResult>(),
            Error: null);
    }

    private sealed class FakeStatusService(IReadOnlyList<InstalledGameSaveStatus> statuses)
        : IInstalledGameSaveStatusService
    {
        public Task<IReadOnlyList<InstalledGameSaveStatus>> GetInstalledGameStatusesAsync(
            System.Threading.CancellationToken cancellationToken = default)
        {
            return Task.FromResult(statuses);
        }
    }
}
