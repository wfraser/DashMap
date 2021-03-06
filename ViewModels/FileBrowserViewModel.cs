﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.Storage;

namespace Breadcrumbs.ViewModels
{
    public enum FileBrowserMode
    {
        Open,
        Save
    };

    public class FileBrowserViewModel : ViewModelBase
    {
        // TODO: put these in the localized strings file
        private static readonly string OpenString = "Open";
        private static readonly string LoadString = "Load";
        private static readonly string SaveString = "Save";

        public MainViewModel MainVM
        {
            get { return m_mainVM; }
        }
        private MainViewModel m_mainVM;

        public string DefaultFileExtension
        {
            get { return m_defaultFileExtension; }
            set { m_defaultFileExtension = value; }
        }
        private string m_defaultFileExtension;

        public string DefaultFileName
        {
            get { return m_defaultFileName; }
            set
            {
                m_defaultFileName = value;
                NotifyPropertyChanged("DefaultFileName");
            }
        }
        private string m_defaultFileName;

        public void Dismiss(IStorageFile result)
        {
            IsVisible = false;

            // Navigate back to the starting folder.
            m_folder = m_startingFolder;
            NotifyPropertyChanged("FolderName");
            NotifyPropertyChanged("CanGoUp");
            NotifyPropertyChanged("Items");
            NotifyPropertyChanged("SelectButtonText");

            if (m_onDismissed != null)
            {
                m_onDismissed(this, result);
            }
        }
        public void SetDismissedAction(Action<FileBrowserViewModel, IStorageFile> action)
        {
            m_onDismissed = action;
        }
        private Action<FileBrowserViewModel, IStorageFile> m_onDismissed;

        public string FolderName
        {
            get
            {
                if (m_startingFolder.Path == m_folder.Path)
                {
                    return "\\" + m_folder.Name;
                }
                else
                {
                    // Remove the path to the starting folder.
                    return "\\" + m_folder.Path.Substring(m_startingFolder.Path.Length - m_startingFolder.Name.Length);
                }
            }
        }
        private IStorageFolder m_folder;

        public IStorageFolder StartingFolder
        {
            get { return m_startingFolder; }
            set
            {
                m_startingFolder = value;
                m_folder = value;
                NotifyPropertyChanged("StartingFolder");
                NotifyPropertyChanged("FolderName");
                NotifyPropertyChanged("Items");
                NotifyPropertyChanged("CanGoUp");
            }
        }
        private IStorageFolder m_startingFolder;

        public FileBrowserEntry SelectedItem
        {
            get { return m_selectedItem; }
            set
            {
                m_selectedItem = value;
                NotifyPropertyChanged("SelectButtonText");
            }
        }
        private FileBrowserEntry m_selectedItem;

        public bool CanGoUp
        {
            get
            {
                if (m_startingFolder == null || m_folder == null)
                {
                    return false;
                }

                System.Diagnostics.Debug.Assert(m_folder.Path.StartsWith(m_startingFolder.Path), "Somehow we got outside the root");
                return !(m_folder.Path.Length == m_startingFolder.Path.Length);
            }
        }

        public IEnumerable<FileBrowserEntry> Items
        {
            get
            {
                if (m_folder == null)
                {
                    return null;
                }

                IEnumerable<IStorageItem> items = m_folder.GetItemsAsync().AsTask().Result;

                return items.Select(item =>
                    {
                        try
                        {
                            return new FileBrowserEntry()
                            {
                                IsFolder = item.Attributes.HasFlag(Windows.Storage.FileAttributes.Directory),
                                FileName = item.Name
                            };
                        }
                        catch (Exception)
                        {
                            // Something went wrong accessing the file. Return null, and filter these out later.
                            return null;
                        }
                    })
                    .Where(item => item != null)
                    .OrderBy(item => item.FileName)
                    .OrderByDescending(item => item.IsFolder);
            }
        }

        public FileBrowserMode Mode
        {
            get { return m_mode; }
            set
            {
                m_mode = value;
                NotifyPropertyChanged("Mode");
                NotifyPropertyChanged("SelectButtonText");
            }
        }
        private FileBrowserMode m_mode;

        public bool IsVisible
        {
            get { return m_isVisible; }
            set
            {
                m_isVisible = value;
                NotifyPropertyChanged("IsVisible");
            }
        }
        private bool m_isVisible;

        public FileBrowserViewModel(MainViewModel mainVM)
        {
            m_mainVM = mainVM;
            m_isVisible = false;
            m_folder = null;
            m_startingFolder = null;
            m_defaultFileExtension = null;
            m_selectedItem = null;
        }

        public string SelectButtonText
        {
            get
            {
                if (m_selectedItem != null && m_selectedItem.IsFolder)
                {
                    return OpenString;
                }

                switch (m_mode)
                {
                    case FileBrowserMode.Open:
                        return LoadString;
                    case FileBrowserMode.Save:
                        return SaveString;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }

        public void SelectNewFile(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                Utils.ShowError("Name can't be null/empty.");
                return;
            }

            if (!string.IsNullOrEmpty(m_defaultFileExtension)
                && !name.ToLower().EndsWith(m_defaultFileExtension))
            {
                name += m_defaultFileExtension;
            }

            m_folder.CreateFileAsync(name).AsTask()
                .ContinueWith(prevTask =>
                {
                    IStorageFile result = null;
                    try
                    {
                        result = prevTask.Result;
                    }
                    catch (AggregateException ex)
                    {
                        Utils.ShowError(ex, "Error creating new file");
                    }

                    // Always dismiss, even on error (which gets treated as a Cancel).
                    Dismiss(result);
                });
        }

        public void SelectFile(string name)
        {
            m_folder.GetFileAsync(name).AsTask()
                .ContinueWith(prevTask =>
                {
                    IStorageFile result = null;
                    try
                    {
                        result = prevTask.Result;
                    }
                    catch (AggregateException ex)
                    {
                        Utils.ShowError(ex, "Error returning selection");
                    }

                    // Always dismiss, even on error (which gets treated as a Cancel).
                    Dismiss(result);
                });
        }

        public void NavigateToSubfolder(string name, IStorageFolder baseFolder = null)
        {
            if (baseFolder == null)
            {
                baseFolder = m_folder;
            }

            baseFolder.GetFolderAsync(name).AsTask()
                .ContinueWith(prevTask =>
                {
                    try
                    {
                        m_folder = prevTask.Result;
                        NotifyPropertyChanged("FolderName");
                        NotifyPropertyChanged("CanGoUp");
                        NotifyPropertyChanged("Items");
                        NotifyPropertyChanged("SelectButtonText");
                    }
                    catch (AggregateException ex)
                    {
                        Utils.ShowError(ex, "Error going to sub-folder");
                    }
                });
        }

        public void NavigateUp()
        {
            // This does NOT work:
            //NavigateToSubfolder("..");

            var fullPath = Path.GetDirectoryName(m_folder.Path);
            if (fullPath == m_startingFolder.Path)
            {
                // Special case: we're navigating up to the starting folder.
                m_folder = m_startingFolder;
                NotifyPropertyChanged("FolderName");
                NotifyPropertyChanged("CanGoUp");
                NotifyPropertyChanged("Items");
                NotifyPropertyChanged("SelectButtonText");
            }
            else
            {
                var subFolder = fullPath.Substring(m_startingFolder.Path.Length + 1);
                NavigateToSubfolder(subFolder, m_startingFolder);
            }
        }

        public void DeleteItem(string name, IStorageItem itemConfirmedToDelete = null)
        {
            if (itemConfirmedToDelete == null)
            {
                // Get the storage item.
                m_folder.GetItemAsync(name).AsTask()
                    .ContinueWith(prevTask =>
                    {
                        try
                        {
                            // Request confirmation.

                            var item = prevTask.Result;
                            bool isDir = item.Attributes.HasFlag(Windows.Storage.FileAttributes.Directory);

                            App.RootFrame.Dispatcher.BeginInvoke(() =>
                                {
                                    var result = MessageBox.Show(
                                        "Are you sure you want to delete that "
                                            + (isDir ? "folder and everything in it?"
                                                     : "file?"),
                                        "Confirm Delete",
                                        MessageBoxButton.OKCancel);

                                    if (result != MessageBoxResult.Cancel)
                                    {
                                        // Re-invoke and delete it for real this time.
                                        Task.Run(() => DeleteItem(name, item));
                                    }
                                });
                        }
                        catch (AggregateException ex)
                        {
                            Utils.ShowError(ex, "Error getting item to delete");
                        }
                    });
            }
            else
            {
                // Perform the deletion.
                itemConfirmedToDelete.DeleteAsync().AsTask()
                    .ContinueWith(prevTask =>
                    {
                        try
                        {
                            prevTask.Wait();
                            NotifyPropertyChanged("Items");
                        }
                        catch (AggregateException ex)
                        {
                            Utils.ShowError(ex, "Error deleting item");
                        }
                    });
            }
        }

        public void MakeDirectory(string name)
        {
            m_folder.CreateFolderAsync(name).AsTask()
                .ContinueWith(prevTask =>
                {
                    try
                    {
                        var folder = prevTask.Result;
                        NavigateToSubfolder(folder.Name);
                    }
                    catch (AggregateException ex)
                    {
                        Utils.ShowError(ex, "Error making directory");
                    }
                });
        }

        public bool SyncBusyScreenVisible
        {
            get { return m_syncBusyScreenVisible; }
            set
            {
                m_syncBusyScreenVisible = value;
                NotifyPropertyChanged("SyncBusyScreenVisible");
            }
        }
        private bool m_syncBusyScreenVisible;

        public string SyncProgressDetail
        {
            get { return m_syncProgressDetail; }
            set
            {
                m_syncProgressDetail = value;
                NotifyPropertyChanged("SyncProgressDetail");
            }
        }
        private string m_syncProgressDetail;

        public bool SyncProgressIsIndeterminate
        {
            get { return (m_syncProgressValue < 0); }
        }

        public double SyncProgressValue
        {
            get
            {
                if (m_syncProgressValue < 0)
                    return 0.0;
                else
                    return m_syncProgressValue;
            }
            set
            {
                m_syncProgressValue = value;
                NotifyPropertyChanged("SyncProgressIsIndeterminate");
                NotifyPropertyChanged("SyncProgressValue");
            }
        }
        private double m_syncProgressValue;

        public void CloudSync()
        {
            SyncProgressValue = -1;
            SyncBusyScreenVisible = true;

            // The browser window LoginAsync opens is forced into portrait, but the keyboard is
            // stuck in landscape mode, and it looks completely stupid.
            // So, we have to force portrait mode for the duration of the auth procedure.
            m_mainVM.ForcePortraitMode(true);

            Breadcrumbs.CloudSync.Authenticate()
                .ContinueWith(authTask =>
                    {
                        m_mainVM.ForcePortraitMode(false);

                        if (authTask.Result != null)
                        {
                            Action<double, string> progressUpdate =
                                (progress, detail) =>
                                {
                                    SyncProgressValue = progress;
                                    SyncProgressDetail = detail;
                                };

                            try
                            {
                                CloudSync.Summary result = m_mainVM.CloudSync.Synchronize(progressUpdate).Result;
                                App.RootFrame.Dispatcher.BeginInvoke(() =>
                                MessageBox.Show(
                                    string.Format("Synchronization Complete.\n\n"
                                        + "{0} downloaded\n"
                                        + "{1} uploaded\n"
                                        + "{2} up to date\n"
                                        + "{3} deleted locally\n"
                                        + "{4} deleted from OneDrive",
                                        result.Downloaded, result.Uploaded, result.UpToDate, result.DeletedLocal, result.DeletedCloud),
                                    "Breadcrumbs Cloud Sync",
                                    MessageBoxButton.OK));
                            }
                            catch (Exception ex)
                            {
                                Utils.ShowError(ex);
                            }

                            NotifyPropertyChanged("Items");
                        }
                        else
                        {
                            App.RootFrame.Dispatcher.BeginInvoke(() => MessageBox.Show("Sync cancelled."));
                        }

                        SyncBusyScreenVisible = false;
                    });
        }

        #region File Browser Internal Classes

        public class FileBrowserEntry
        {
            public bool IsFolder
            {
                get;
                set;
            }

            public string FileName
            {
                get;
                set;
            }
        }

        public class FileBrowserEntryEnumerator : IEnumerator<FileBrowserEntry>
        {
            public FileBrowserEntry Current
            {
                get { return m_current; }
            }
            private FileBrowserEntry m_current;

            object System.Collections.IEnumerator.Current
            {
                get { return m_current; }
            }

            public FileBrowserEntryEnumerator(IEnumerable<IStorageItem> items)
            {
                m_items = items.GetEnumerator();
                disposed = false;
            }

            public bool MoveNext()
            {
                bool hasCurrent = m_items.MoveNext();
                if (hasCurrent)
                {
                    m_current = new FileBrowserEntry()
                    {
                        IsFolder = m_items.Current.Attributes.HasFlag(Windows.Storage.FileAttributes.Directory),
                        FileName = m_items.Current.Name
                    };
                }
                else
                {
                    m_current = null;
                }
                return hasCurrent;
            }

            public void Reset()
            {
                m_items.Reset();
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!disposed && disposing)
                {
                    m_items.Dispose();
                }
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private bool disposed;
            private IEnumerator<IStorageItem> m_items;
        }

        #endregion File Browser Internal Classes
    }
}
