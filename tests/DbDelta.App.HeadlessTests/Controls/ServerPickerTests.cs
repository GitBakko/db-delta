using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.App.Views.Controls;
using DbDelta.Persistence.Sql;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The server field used to be an AutoCompleteBox that filtered its own list by
/// the text it had just written: after one pick the popup matched that single
/// name, so correcting a wrong server was impossible, and wiping the field took
/// the app down. These assert the two halves of the replacement — browse and
/// type — stay independent.
/// </summary>
public class ServerPickerTests
{
    private static (ProjectSetupDialog Dialog, ProjectSetupViewModel Vm) Open()
    {
        ProjectSetupViewModel vm = new();
        vm.Source.SeedRecentServers([("SRV-A", "10.0.0.1"), ("SRV-B", "10.0.0.2")]);
        ProjectSetupDialog dlg = new() { DataContext = vm };
        dlg.Show();
        Dispatcher.UIThread.RunJobs();
        return (dlg, vm);
    }

    private static (ServerPicker Picker, ComboBox List, TextBox Box) Parts(ProjectSetupDialog dlg)
    {
        ServerPicker picker = dlg.FindControl<ServerPicker>("SrcServerPicker")!;
        return (picker,
                picker.FindControl<ComboBox>("PART_ServerList")!,
                picker.FindControl<TextBox>("PART_ServerName")!);
    }

    [AvaloniaFact]
    public void PickingFromTheList_FillsTheNameField()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, TextBox box) = Parts(dlg);

        list.SelectedItem = vm.Source.ServerSuggestions[1];
        Dispatcher.UIThread.RunJobs();

        vm.Source.ServerName.Should().Be("SRV-A");
        box.Text.Should().Be("SRV-A");
    }

    /// <summary>The bug as reported: a wrong pick has to be correctable.</summary>
    [AvaloniaFact]
    public void AfterAPick_EveryOtherServerIsStillInTheList()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, _) = Parts(dlg);
        int total = vm.Source.ServerSuggestions.Count;

        list.SelectedItem = vm.Source.ServerSuggestions[1];
        Dispatcher.UIThread.RunJobs();

        list.ItemCount.Should().Be(total);

        list.SelectedItem = vm.Source.ServerSuggestions[2];
        Dispatcher.UIThread.RunJobs();
        vm.Source.ServerName.Should().Be("SRV-B");
    }

    [AvaloniaFact]
    public void ClearingTheNameField_LeavesTheListIntactAndDeselected()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, TextBox box) = Parts(dlg);

        list.SelectedItem = vm.Source.ServerSuggestions[1];
        Dispatcher.UIThread.RunJobs();

        box.Text = string.Empty;
        Dispatcher.UIThread.RunJobs();

        vm.Source.ServerName.Should().BeEmpty();
        list.SelectedItem.Should().BeNull();
        list.ItemCount.Should().Be(vm.Source.ServerSuggestions.Count);
    }

    /// <summary>
    /// A server on another subnet — or behind a disabled SQL Browser — never
    /// shows up in the list, so typing has to keep working.
    /// </summary>
    [AvaloniaFact]
    public void AServerAbsentFromTheList_CanStillBeTyped()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, TextBox box) = Parts(dlg);

        box.Text = "192.168.3.243";
        Dispatcher.UIThread.RunJobs();

        vm.Source.ServerName.Should().Be("192.168.3.243");
        list.SelectedItem.Should().BeNull("nothing in the list matches what was typed");
    }

    [AvaloniaFact]
    public void SectionHeaders_AreNotSelectable()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, _) = Parts(dlg);

        list.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        vm.Source.ServerSuggestions[0].IsHeaderOnly.Should().BeTrue();
        list.ContainerFromIndex(0)!.IsEnabled.Should().BeFalse();
        list.ContainerFromIndex(1)!.IsEnabled.Should().BeTrue();
    }

    /// <summary>A rescan must not leave the field pointing at a stale row.</summary>
    [AvaloniaFact]
    public void RescanKeepsTheSelectionOnTheSameServer()
    {
        (ProjectSetupDialog dlg, ProjectSetupViewModel vm) = Open();
        (_, ComboBox list, _) = Parts(dlg);

        list.SelectedItem = vm.Source.ServerSuggestions[1];
        Dispatcher.UIThread.RunJobs();

        vm.Source.ApplyScanResults([new DiscoveredServer("SRV-C", "10.0.0.3")]);
        Dispatcher.UIThread.RunJobs();

        vm.Source.ServerName.Should().Be("SRV-A");
        (list.SelectedItem as DiscoveredServer)!.Name.Should().Be("SRV-A");
    }
}
