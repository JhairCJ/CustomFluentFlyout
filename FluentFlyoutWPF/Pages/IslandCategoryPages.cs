// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Pages;

public sealed class IslandGeneralPage : IslandPage
{
    public IslandGeneralPage() : base(IslandCategory.General) { }
}

public sealed class IslandMusicPage : IslandPage
{
    public IslandMusicPage() : base(IslandCategory.Music) { }
}

public sealed class IslandScreensPage : IslandPage
{
    public IslandScreensPage() : base(IslandCategory.Screens) { }
}

public sealed class IslandInteractionPage : IslandPage
{
    public IslandInteractionPage() : base(IslandCategory.Interaction) { }
}

public sealed class IslandAppearancePage : IslandPage
{
    public IslandAppearancePage() : base(IslandCategory.Appearance) { }
}

public sealed class IslandContentPage : IslandPage
{
    public IslandContentPage() : base(IslandCategory.Content) { }
}

public sealed class IslandTimerPage : IslandPage
{
    public IslandTimerPage() : base(IslandCategory.Timer) { }
}

public sealed class IslandAppsPage : IslandPage
{
    public IslandAppsPage() : base(IslandCategory.Apps) { }
}

public sealed class IslandOrganizationPage : IslandPage
{
    public IslandOrganizationPage() : base(IslandCategory.Organization) { }
}

public sealed class IslandToolsPage : IslandPage
{
    public IslandToolsPage() : base(IslandCategory.Tools) { }
}
