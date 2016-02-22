﻿using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using VLC_WinRT.Model.Video;
using VLC_WinRT.ViewModels;
using Windows.UI.Xaml.Navigation;

namespace VLC_WinRT.UI.Legacy.Views.MusicPages.ArtistPageControls
{
    public sealed partial class ArtistPageBase : Page
    {
        public ArtistPageBase()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            App.SetShellDecoration(true, true);
            Window.Current.SetTitleBar(TitleBar);
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            App.SetShellDecoration(false);
        }
    }
}