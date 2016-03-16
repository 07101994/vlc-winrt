﻿using Windows.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI.Core;
using Autofac;
using VLC_WinRT.Model;
using VLC_WinRT.Model.Music;
using VLC_WinRT.Services.RunTime;
using VLC_WinRT.ViewModels;
using VLC_WinRT.ViewModels.MusicVM;
using WinRTXamlToolkit.IO.Extensions;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using VLC_WinRT.Utils;
using VLC_WinRT.Views.MusicPages.PlaylistControls;
using WinRTXamlToolkit.Controls.Extensions;
using System.Collections.Generic;
using libVLCX;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Windows.Foundation;
using VLC_WinRT.Database;

namespace VLC_WinRT.Helpers.MusicLibrary
{
    public class MusicLibrary
    {
        #region properties
        private bool _alreadyIndexedOnce = false;

        #endregion
        #region databases
        readonly ArtistDatabase artistDatabase = new ArtistDatabase();
        readonly TrackDatabase trackDatabase = new TrackDatabase();
        readonly AlbumDatabase albumDatabase = new AlbumDatabase();
        readonly TracklistItemRepository tracklistItemRepository = new TracklistItemRepository();
        readonly TrackCollectionRepository trackCollectionRepository = new TrackCollectionRepository();
        #endregion
        #region collections
        public SmartCollection<ArtistItem> Artists { get; private set; }
        public SmartCollection<AlbumItem> Albums { get; private set; }
        public SmartCollection<TrackItem> Tracks { get; private set; }
        public SmartCollection<TrackCollection> TrackCollections { get; private set; }
        #endregion
        #region mutexes
        public TaskCompletionSource<bool> ContinueIndexing { get; set; }
        public TaskCompletionSource<bool> MusicCollectionLoaded = new TaskCompletionSource<bool>();

        readonly SemaphoreSlim AlbumCoverFetcherSemaphoreSlim = new SemaphoreSlim(4);
        readonly SemaphoreSlim ArtistPicFetcherSemaphoreSlim = new SemaphoreSlim(4);
        readonly SemaphoreSlim TrackItemDiscovererSemaphoreSlim = new SemaphoreSlim(1);

        public async Task FetchAlbumCoverOrWaitAsync(AlbumItem albumItem)
        {
            await AlbumCoverFetcherSemaphoreSlim.WaitAsync();
            try
            {
                await albumItem.LoadPicture();
            }
            finally
            {
                AlbumCoverFetcherSemaphoreSlim.Release();
            }
        }

        public async Task FetchArtistPicOrWaitAsync(ArtistItem artistItem)
        {
            await ArtistPicFetcherSemaphoreSlim.WaitAsync();
            try
            {
                Debug.WriteLine($"{DateTime.Now} -- loading pic : {artistItem.Name}");
                await artistItem.LoadPicture();
                Debug.WriteLine($"{DateTime.Now} -- loading operation DONE: {artistItem.Name}");
            }
            catch
            {
                ArtistPicFetcherSemaphoreSlim.Release();
            }
            finally
            {
                ArtistPicFetcherSemaphoreSlim.Release();
            }
        }

        async Task DiscoverTrackItemOrWaitAsync(StorageFile storageItem)
        {
            await TrackItemDiscovererSemaphoreSlim.WaitAsync();
            try
            {
                if (!await trackDatabase.DoesTrackExist(storageItem.Path))
                {
                    await CreateDatabaseFromMusicFile(storageItem);
                }
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
                TrackItemDiscovererSemaphoreSlim.Release();
            }
            finally
            {
                TrackItemDiscovererSemaphoreSlim.Release();
            }
        }
        #endregion

        #region Load Collections from DB
        public void DropTablesIfNeeded()
        {
            if (!Numbers.NeedsToDrop()) return;
            trackCollectionRepository.Drop();
            tracklistItemRepository.Drop();
            albumDatabase.Drop();
            artistDatabase.Drop();
            trackDatabase.Drop();
            trackCollectionRepository.Initialize();
            tracklistItemRepository.Initialize();
            albumDatabase.Initialize();
            artistDatabase.Initialize();
            trackDatabase.Initialize();
        }

        public async Task PerformRoutineCheckIfNotBusy()
        {
            // Routine check to add new files if there are new ones
            //if (!IsBusy)
            //{
            //    await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Low, () =>
            //    {
            //        IsBusy = true;
            //    });
                   await StartIndexing();
            //    await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Low, () =>
            //    {
            //        IsBusy = false;
            //        Locator.MainVM.InformationText = "";
            //    });
            //}
        }

        public async Task Initialize()
        {
            Artists = new SmartCollection<ArtistItem>();
            Albums = new SmartCollection<AlbumItem>();
            Tracks = new SmartCollection<TrackItem>();
            TrackCollections = new SmartCollection<TrackCollection>();

            if (_alreadyIndexedOnce) return;
            _alreadyIndexedOnce = true;
            // Doing full indexing from scratch if 0 tracks are found
            if (await IsMusicDatabaseEmpty())
            {
                await StartIndexing();
            }
            else
            {
                // Else, perform a Routine Indexing (without dropping tables)
                await PerformMusicLibraryIndexing();
            }
        }

        async Task StartIndexing()
        {
            artistDatabase.DeleteAll();
            albumDatabase.DeleteAll();
            trackDatabase.DeleteAll();
            trackCollectionRepository.DeleteAll();
            tracklistItemRepository.DeleteAll();

            artistDatabase.Initialize();
            albumDatabase.Initialize();
            trackDatabase.Initialize();
            trackCollectionRepository.Initialize();
            tracklistItemRepository.Initialize();

            Artists?.Clear();
            Albums?.Clear();
            Tracks?.Clear();
            TrackCollections?.Clear();
            await PerformMusicLibraryIndexing();
        }
        
        public async Task LoadAlbumsFromDatabase()
        {
            try
            {
                Albums?.Clear();
                LogHelper.Log("Loading albums from MusicDB ...");
                var albums = await albumDatabase.LoadAlbums().ToObservableAsync();
                var orderedAlbums = albums.OrderBy(x => x.Artist).ThenBy(x => x.Name);
                Albums.AddRange(orderedAlbums);
            }
            catch
            {
                LogHelper.Log("Error selecting albums from database.");
            }
        }

        public async Task<ObservableCollection<AlbumItem>> LoadRandomAlbumsFromDatabase()
        {
            try
            {
                var favAlbums = await albumDatabase.LoadAlbums(x => x.Favorite).ToObservableAsync();
                if (favAlbums?.Count < 3) // TODO : Magic number
                {
                    var nonfavAlbums = await albumDatabase.LoadAlbums(x => x.Favorite == false).ToObservableAsync();
                    if (nonfavAlbums.Count > 1)
                    {
                        int total = nonfavAlbums.Count - 1;
                        for (int i = 0; i < total; i++)
                        {
                            favAlbums.Add(nonfavAlbums[i]);
                        }
                    }
                }
                return favAlbums;
            }
            catch (Exception)
            {
                LogHelper.Log("Error selecting random albums from database.");
            }
            return new ObservableCollection<AlbumItem>();
        }


        public Task<List<AlbumItem>> Contains(string column, string value)
        {
            return albumDatabase.Contains(column, value);
        }

        public async Task LoadArtistsFromDatabase()
        {
            try
            {
                Artists?.Clear();
                LogHelper.Log("Loading artists from MusicDB ...");
                var artists = await LoadArtists(null);
                LogHelper.Log("Found " + artists.Count + " artists from MusicDB");
                Artists.AddRange(artists.OrderBy(x => x.Name).ToObservable());
            }
            catch { }
        }

        public async Task<ObservableCollection<ArtistItem>> LoadRandomArtistsFromDatabase()
        {
            try
            {
                var topArtists = (await LoadArtists(x => x.PlayCount > 10).ToObservableAsync()).Take(20);
                // We use user top artists to search for similar artists in its collection, to recommend them
                if (topArtists.Any())
                {
                    var random = new Random().Next(0, topArtists.Count() - 1);
                    var suggestedArtists = await MusicFlow.GetFollowingArtistViaSimilarity(topArtists.ElementAt(random));
                    if (suggestedArtists != null)
                        return new ObservableCollection<ArtistItem>(suggestedArtists);
                }
            }
            catch (Exception)
            {
                LogHelper.Log("Error selecting random and recommended artists from database.");
            }
            return new ObservableCollection<ArtistItem>();
        }

        public async Task LoadTracksFromDatabase()
        {
            try
            {
                Tracks = await trackDatabase.LoadTracks().ToObservableAsync();
            }
            catch (Exception)
            {
                LogHelper.Log("Error selecting tracks from database.");
            }
        }

        public Task<bool> IsMusicDatabaseEmpty()
        {
            return trackDatabase.IsEmpty();
        }

        public async Task LoadPlaylistsFromDatabase()
        {
            try
            {
                var trackColl = await trackCollectionRepository.LoadTrackCollections().ToObservableAsync();
                foreach (var trackCollection in trackColl)
                {
                    var observableCollection = await tracklistItemRepository.LoadTracks(trackCollection);
                    foreach (TracklistItem tracklistItem in observableCollection)
                    {
                        TrackItem item = await trackDatabase.LoadTrack(tracklistItem.TrackId);
                        trackCollection.Playlist.Add(item);
                    }
                }
                TrackCollections = trackColl;
            }
            catch (Exception)
            {
                LogHelper.Log("Error getting database.");
            }
        }
        #endregion

        #region Music Library Indexation Logic

        async Task PerformMusicLibraryIndexing()
        {
            try
            {
#if WINDOWS_PHONE_APP
                await GetAllMusicFolders();
                return;
#else
                IReadOnlyList<StorageFile> files = null;
                var queryOptions = new QueryOptions { FolderDepth = FolderDepth.Deep };
                foreach (var type in VLCFileExtensions.Supported)
                    queryOptions.FileTypeFilter.Add(type);
                var fileQueryResult = KnownFolders.MusicLibrary.CreateFileQueryWithOptions(queryOptions);
                files = await fileQueryResult.GetFilesAsync();
#endif
                var sw = new Stopwatch();
                sw.Start();
                foreach (var item in files)
                {
                    if (ContinueIndexing != null)
                    // We prevent indexing this file and upcoming files when a video is playing
                    {
                        await ContinueIndexing.Task;
                        ContinueIndexing = null;
                    }
                    await DiscoverTrackItemOrWaitAsync(item);
                }
                sw.Stop();
                Debug.WriteLine($"Done discover tracks in {sw.Elapsed.TotalSeconds} seconds");
                Debug.WriteLine($"Indexed : {Tracks.Count}");
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
        }

        async Task GetAllMusicFolders()
        {
#if WINDOWS_PHONE_APP
            try
            {
                StorageFolder musicLibrary = KnownFolders.MusicLibrary;
                LogHelper.Log("Searching for music from Phone MusicLibrary ...");
                await CreateDatabaseFromMusicFolder(musicLibrary);
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
#endif
        }

        async Task CreateDatabaseFromMusicFolder(StorageFolder musicFolder)
        {
            try
            {
                if (ContinueIndexing != null) // We prevent indexing new folder and files recursively when a Video is playing
                {
                    await ContinueIndexing.Task;
                    ContinueIndexing = null;
                }
                if (musicFolder.Name != Strings.PodcastFolderName)
                {
                    var folders = await musicFolder.GetFoldersAsync();
                    if (folders.Any())
                    {
                        foreach (var storageFolder in folders)
                        {
                            await CreateDatabaseFromMusicFolder(storageFolder);
                        }
                    }
                    var folderFiles = await musicFolder.GetFilesAsync();
                    if (folderFiles != null && folderFiles.Any())
                    {
                        foreach (var storageFile in folderFiles)
                        {
                            await DiscoverTrackItemOrWaitAsync(storageFile);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
        }

        async Task CreateDatabaseFromMusicFile(StorageFile item)
        {
            try
            {
                if (!VLCFileExtensions.AudioExtensions.Contains(item.FileType.ToLower())) return;
                var media = Locator.VLCService.GetMediaFromPath(item.Path);
                var mP = Locator.VLCService.GetMusicProperties(media);
                if (mP == null || (string.IsNullOrEmpty(mP.Artist) && string.IsNullOrEmpty(mP.Album) && (string.IsNullOrEmpty(mP.Title) || mP.Title == item.Name)))
                {
                    var props = await item.Properties.GetMusicPropertiesAsync();
                    mP = new MediaProperties()
                    {
                        Album = props.Album,
                        AlbumArtist = props.AlbumArtist,
                        Artist = props.Artist,
                        Title = props.Title,
                        Tracknumber = props.TrackNumber,
                        Genre = (props.Genre != null && props.Genre.Any()) ? props.Genre[0] : null,
                    };
                }
                if (mP != null)
                {
                    var artistName = mP.Artist?.Trim();
                    var albumArtistName = mP.AlbumArtist?.Trim();
                    ArtistItem artist = await LoadViaArtistName(string.IsNullOrEmpty(albumArtistName) ? artistName : albumArtistName);
                    if (artist == null)
                    {
                        artist = new ArtistItem();
                        artist.Name = string.IsNullOrEmpty(albumArtistName) ? artistName : albumArtistName;
                        artist.PlayCount = 0;
                        await artistDatabase.Add(artist);
                        AddArtist(artist);
                    }

                    var albumName = mP.Album?.Trim();
                    var albumYear = mP.Year;
                    AlbumItem album = await albumDatabase.LoadAlbumViaName(artist.Id, albumName);
                    if (album == null)
                    {
                        var albumUrl = Locator.VLCService.GetAlbumUrl(media);
                        string albumSimplifiedUrl = null;
                        if (!string.IsNullOrEmpty(albumUrl) && albumUrl.StartsWith("file://"))
                        {
                            // The Uri will be like
                            // ms-appdata:///local/vlc/art/artistalbum/30 Seconds To Mars/B-sides & Rarities/art.jpg
                            var indexStart = albumUrl.IndexOf("vlc/art/artistalbum/", StringComparison.Ordinal);
                            if (indexStart != -1)
                            {
                                albumSimplifiedUrl = albumUrl.Substring(indexStart, albumUrl.Length - indexStart);
                                Debug.WriteLine("VLC_WinRT : found album cover with TagLib - " + albumName);
                            }
                        }

                        album = new AlbumItem
                        {
                            Name = string.IsNullOrEmpty(albumName) ? string.Empty : albumName,
                            AlbumArtist = albumArtistName,
                            Artist = string.IsNullOrEmpty(albumArtistName) ? artistName : albumArtistName,
                            ArtistId = artist.Id,
                            Favorite = false,
                            Year = albumYear,
                            AlbumCoverUri = albumSimplifiedUrl
                        };
                        await albumDatabase.Add(album);
                        AddAlbum(album);
                        await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Low, () =>
                        {
                            Locator.MainVM.InformationText = string.Format(Strings.AlbumsFound, Locator.MusicLibrary.Albums.Count);
                        });
                    }

                    TrackItem track = new TrackItem
                    {
                        AlbumId = album.Id,
                        AlbumName = album.Name,
                        ArtistId = artist.Id,
                        ArtistName = artistName,
                        CurrentPosition = 0,
                        Duration = mP.Duration,
                        Favorite = false,
                        Name = string.IsNullOrEmpty(mP.Title) ? item.DisplayName : mP.Title,
                        Path = item.Path,
                        Index = mP.Tracknumber,
                        DiscNumber = mP.DiscNumber,
                        Genre = mP.Genre
                    };
                    await trackDatabase.Add(track);
                    AddTrack(track);
                }
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
        }
        #endregion

        public void AddArtist(ArtistItem artist)
        {
            Artists.Add(artist);
        }

        public void AddAlbum(AlbumItem album)
        {
            Albums.Add(album);
        }

        public void AddTrack(TrackItem track)
        {
            Tracks.Add(track);
        }


        public ObservableCollection<GroupItemList<AlbumItem>> OrderAlbums(OrderType orderType, OrderListing orderListing)
        {
            if (Albums == null) return null;
            var groupedAlbums = new ObservableCollection<GroupItemList<AlbumItem>>();
            if (orderType == OrderType.ByArtist)
            {
                if (orderListing == OrderListing.Ascending)
                {
                    var groupQuery = from album in Albums
                                     group album by Strings.HumanizedArtistName(album.Artist) into a
                                     orderby a.Key
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
                else if (orderListing == OrderListing.Descending)
                {
                    var groupQuery = from album in Albums
                                     group album by Strings.HumanizedArtistName(album.Artist) into a
                                     orderby a.Key descending
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
            }
            else if (orderType == OrderType.ByDate)
            {
                if (orderListing == OrderListing.Ascending)
                {
                    var groupQuery = from album in Albums
                                     orderby album.Year
                                     group album by Strings.HumanizedYear(album.Year) into a
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
                else if (orderListing == OrderListing.Descending)
                {
                    var groupQuery = from album in Albums
                                     orderby album.Year descending
                                     group album by Strings.HumanizedYear(album.Year) into a
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
            }
            else if (orderType == OrderType.ByAlbum)
            {
                if (orderListing == OrderListing.Ascending)
                {
                    var groupQuery = from album in Albums
                                     group album by Strings.HumanizedAlbumFirstLetter(album.Name) into a
                                     orderby a.Key
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
                else if (orderListing == OrderListing.Descending)
                {
                    var groupQuery = from album in Albums
                                     group album by Strings.HumanizedAlbumFirstLetter(album.Name) into a
                                     orderby a.Key descending
                                     select new { GroupName = a.Key, Items = a };
                    foreach (var g in groupQuery)
                    {
                        GroupItemList<AlbumItem> albums = new GroupItemList<AlbumItem>();
                        albums.Key = g.GroupName;
                        foreach (var album in g.Items)
                        {
                            albums.Add(album);
                        }
                        groupedAlbums.Add(albums);
                    }
                }
            }
            return groupedAlbums;
        }

        public ObservableCollection<GroupItemList<ArtistItem>> OrderArtists()
        {
            var groupedArtists = new ObservableCollection<GroupItemList<ArtistItem>>();
            var groupQuery = from artist in Artists
                             group artist by Strings.HumanizedArtistFirstLetter(artist.Name) into a
                             orderby a.Key
                             select new { GroupName = a.Key, Items = a };
            foreach (var g in groupQuery)
            {
                GroupItemList<ArtistItem> artists = new GroupItemList<ArtistItem>();
                artists.Key = g.GroupName;
                foreach (var artist in g.Items)
                {
                    artists.Add(artist);
                }
                groupedArtists.Add(artists);
            }
            return groupedArtists;
        }

        public IEnumerable<IGrouping<char, TrackItem>> OrderTracks()
        {
            return Tracks?.GroupBy(x => string.IsNullOrEmpty(x.Name) ? Strings.UnknownChar : (char.IsLetter(x.Name.ElementAt(0)) ? x.Name.ToUpper().ElementAt(0) : Strings.UnknownChar));
        }


        public async Task AddNewPlaylist(string trackCollectionName)
        {
            if (string.IsNullOrEmpty(trackCollectionName)) return;
            TrackCollection trackCollection = null;
            trackCollection = await trackCollectionRepository.LoadFromName(trackCollectionName);
            if (trackCollection != null)
            {
                await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () => ToastHelper.Basic(Strings.PlaylistAlreadyExists));
            }
            else
            {
                trackCollection = new TrackCollection();
                trackCollection.Name = trackCollectionName;
                await trackCollectionRepository.Add(trackCollection);
                TrackCollections.Add(trackCollection);
            }
        }

        public Task DeletePlaylistTrack(TrackItem track, TrackCollection trackCollection)
        {
            return tracklistItemRepository.Remove(track.Id, trackCollection.Id);
        }

        public async Task DeletePlaylist(TrackCollection trackCollection)
        {
            await trackCollectionRepository.Remove(trackCollection);
            await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () =>
            {
                TrackCollections.Remove(trackCollection);
            });
        }

        public async Task AddToPlaylist(TrackItem trackItem, bool displayToastNotif = true)
        {
            if (Locator.MusicLibraryVM.CurrentTrackCollection == null) return;
            if (Locator.MusicLibraryVM.CurrentTrackCollection.Playlist.Contains(trackItem))
            {
                ToastHelper.Basic(Strings.TrackAlreadyExistsInPlaylist);
                return;
            }
            Locator.MusicLibraryVM.CurrentTrackCollection.Playlist.Add(trackItem);
            await tracklistItemRepository.Add(new TracklistItem()
            {
                TrackId = trackItem.Id,
                TrackCollectionId = Locator.MusicLibraryVM.CurrentTrackCollection.Id,
            });
            if (displayToastNotif)
                ToastHelper.Basic(string.Format(Strings.TrackAddedToYourPlaylist, trackItem.Name));
        }

        public async Task AddToPlaylist(AlbumItem albumItem)
        {
            if (Locator.MusicLibraryVM.CurrentTrackCollection == null) return;
            var playlistId = Locator.MusicLibraryVM.CurrentTrackCollection.Id;
            foreach (TrackItem trackItem in albumItem.Tracks)
            {
                Locator.MusicLibraryVM.CurrentTrackCollection.Playlist.Add(trackItem);
                await tracklistItemRepository.Add(new TracklistItem()
                {
                    TrackId = trackItem.Id,
                    TrackCollectionId = playlistId,
                });
            }
            ToastHelper.Basic(string.Format(Strings.TrackAddedToYourPlaylist, albumItem.Name));
        }

        public async Task UpdateTrackCollection(TrackCollection trackCollection)
        {
            var loadTracks = await tracklistItemRepository.LoadTracks(trackCollection);
            foreach (TracklistItem tracklistItem in loadTracks)
            {
                await tracklistItemRepository.Remove(tracklistItem);
            }
            foreach (TrackItem trackItem in trackCollection.Playlist)
            {
                var trackListItem = new TracklistItem { TrackId = trackItem.Id, TrackCollectionId = trackCollection.Id };
                await tracklistItemRepository.Add(trackListItem);
            }
        }

        public async Task RemoveTrackFromCollectionAndDatabase(TrackItem trackItem)
        {
            await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    trackDatabase.Remove(Tracks.FirstOrDefault(x => x.Path == trackItem.Path));
                    Tracks.Remove(Tracks.FirstOrDefault(x => x.Path == trackItem.Path));
                    var album = Albums.FirstOrDefault(x => x.Id == trackItem.AlbumId);
                    album?.Tracks.Remove(album.Tracks.FirstOrDefault(x => x.Path == trackItem.Path));

                    var artist = Artists.FirstOrDefault(x => x.Id == trackItem.ArtistId);
                    var artistalbum = artist?.Albums.FirstOrDefault(x => x.Id == trackItem.AlbumId);
                    artistalbum?.Tracks.Remove(artistalbum.Tracks.FirstOrDefault(x => x.Path == trackItem.Path));
                    if (album.Tracks.Count == 0)
                    {
                        // We should remove the album as a whole
                        Albums.Remove(album);
                        albumDatabase.Remove(album);
                        artist.Albums.Remove(artistalbum);
                    }
                    var playingTrack = Locator.MediaPlaybackViewModel.TrackCollection.Playlist.FirstOrDefault(x => x.Id == trackItem.Id);
                    if (playingTrack != null) Locator.MediaPlaybackViewModel.TrackCollection.Playlist.Remove(playingTrack);
                }
                catch
                {
                }
            });
        }

        public bool AddAlbumToPlaylist(object args)
        {
            if (Locator.MusicLibraryVM.CurrentTrackCollection == null)
            {
#if WINDOWS_PHONE_APP
                if (args is ContentDialogButtonClickEventArgs)
                    ((ContentDialogButtonClickEventArgs)args).Cancel = true;
#endif
                ToastHelper.Basic(Strings.HaveToSelectPlaylist, false, "selectplaylist");
                return false;
            }
#if WINDOWS_APP
            var flyout = ((AddAlbumToPlaylistBase)args).GetFirstDescendantOfType<SettingsFlyout>();
            flyout?.Hide();
#endif
            Locator.MusicLibraryVM.AddToPlaylistCommand.Execute(Locator.MusicLibraryVM.CurrentAlbum);
            return true;
        }

        public async Task<TrackItem> GetTrackItemFromFile(StorageFile track, string token = null)
        {
            //TODO: Warning, is it safe to consider this a good idea?
            var trackItem = await trackDatabase.LoadTrackByPath(track.Path);
            if (trackItem != null)
            {
                return trackItem;
            }

            MusicProperties trackInfos = null;
            try
            {
                trackInfos = await track.Properties.GetMusicPropertiesAsync();
            }
            catch
            {

            }
            trackItem = new TrackItem
            {
                ArtistName = (string.IsNullOrEmpty(trackInfos?.Artist)) ? Strings.UnknownArtist : trackInfos?.Artist,
                AlbumName = trackInfos?.Album ?? Strings.UnknownAlbum,
                Name = (string.IsNullOrEmpty(trackInfos?.Title)) ? track.DisplayName : trackInfos?.Title,
                Path = track.Path,
                Duration = trackInfos?.Duration ?? TimeSpan.Zero,
                File = track,
            };
            if (!string.IsNullOrEmpty(token))
            {
                trackItem.Token = token;
            }
            return trackItem;
        }

        public async Task PopulateTracks(AlbumItem album)
        {
            try
            {
                var tracks = await trackDatabase.LoadTracksByAlbumId(album.Id);
                await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () =>
                {
                    album.Tracks = tracks;
                });
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
        }

        public async Task PopulateAlbums(ArtistItem artist)
        {
            try
            {
                var albums = await albumDatabase.LoadAlbumsFromId(artist.Id).ToObservableAsync();
                await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () =>
                {
                    artist.Albums = albums;
                });
            }
            catch (Exception e)
            {
                LogHelper.Log(StringsHelper.ExceptionToString(e));
            }
        }

        public async Task PopulateAlbumsWithTracks(ArtistItem artist)
        {
            try
            {
                var albums = await albumDatabase.LoadAlbumsFromIdWithTracks(artist.Id).ToObservableAsync();
                var groupedAlbums = new ObservableCollection<GroupItemList<TrackItem>>();
                var groupQuery = from album in albums
                                 orderby album.Name
                                 group album.Tracks by album into a
                                 select new { GroupName = a.Key, Items = a };
                foreach (var g in groupQuery)
                {
                    GroupItemList<TrackItem> tracks = new GroupItemList<TrackItem>();
                    tracks.Key = g.GroupName;
                    foreach (var track in g.Items)
                    {
                        tracks.AddRange(track);
                    }
                    groupedAlbums.Add(tracks);
                }

                await DispatchHelper.InvokeAsync(CoreDispatcherPriority.Normal, () =>
                {
                    artist.Albums = albums;
                    artist.AlbumsGrouped = groupedAlbums;
                });
            }
            catch { }
        }

        public Task<List<TracklistItem>> LoadTracks(TrackCollection trackCollection)
        {
            return tracklistItemRepository.LoadTracks(trackCollection);
        }

        public Task<TrackItem> LoadTrackById(int id)
        {
            return trackDatabase.LoadTrack(id);
        }

        public Task<List<TrackItem>> LoadTracksByArtistId(int id)
        {
            return trackDatabase.LoadTracksByArtistId(id);
        }

        public async Task<List<TrackItem>> LoadTracksByAlbumId(int id)
        {
            return await trackDatabase.LoadTracksByAlbumId(id);
        }

        public async Task<ArtistItem> LoadArtist(int id)
        {
            return await artistDatabase.LoadArtist(id);
        }

        public async Task<ArtistItem> LoadViaArtistName(string name)
        {
            return await artistDatabase.LoadViaArtistName(name);
        }

        public Task<AlbumItem> LoadAlbum(int id)
        {
            return albumDatabase.LoadAlbum(id);
        }

        public Task<List<AlbumItem>> LoadAlbums(int artistId)
        {
            return albumDatabase.LoadAlbumsFromId(artistId);
        }

        public Task<int> LoadAlbumsCount(int artistId)
        {
            return albumDatabase.LoadAlbumsCountFromId(artistId);
        }

        public Task Update(ArtistItem artist)
        {
            return artistDatabase.Update(artist);
        }

        public Task Update(AlbumItem album)
        {
            return albumDatabase.Update(album);
        }

        public Task Update(TrackItem track)
        {
            return trackDatabase.Update(track);
        }

        public Task Remove(TracklistItem tracklist)
        {
            return tracklistItemRepository.Remove(tracklist);
        }

        public Task RemoveTrackInPlaylist(int trackid, int playlistid)
        {
            return tracklistItemRepository.Remove(trackid, playlistid);
        }

        public Task<int> ArtistCount()
        {
            return artistDatabase.Count();
        }

        public Task<ArtistItem> ArtistAt(int index)
        {
            return artistDatabase.At(index);
        }

        public Task<List<ArtistItem>> LoadArtists(Expression<Func<ArtistItem, bool>> predicate)
        {
            return artistDatabase.Load(predicate);
        }

        public Task<List<AlbumItem>> LoadAlbums(Expression<Func<AlbumItem, bool>> predicate)
        {
            return albumDatabase.Load(predicate);
        }

        public Task<List<TrackItem>> LoadTracks()
        {
            return trackDatabase.LoadTracks();
        }
    }
}
