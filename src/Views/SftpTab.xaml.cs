using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SshManager.Views
{
    public partial class SftpTab : UserControl
    {
        public class FileEntry { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public bool IsDirectory { get; set; } }

        public ObservableCollection<FileEntry> LocalItems { get; } = new();
        public ObservableCollection<FileEntry> RemoteItems { get; } = new();

        private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private string _remotePath = "~";

        // SftpService will be injected/assigned by caller
        public SftpService? Sftp { get; set; }

        public SftpTab()
        {
            InitializeComponent();
            LocalList.ItemsSource = LocalItems;
            RemoteList.ItemsSource = RemoteItems;
            Loaded += (_, _) => _ = RefreshLocal();
        }

        private Task RefreshLocal()
        {
            return Task.Run(() =>
            {
                Application.Current.Dispatcher.Invoke(() => LocalItems.Clear());
                try
                {
                    var di = new DirectoryInfo(_localPath);
                    foreach (var fi in di.GetFileSystemInfos())
                    {
                        Application.Current.Dispatcher.Invoke(() => LocalItems.Add(new FileEntry { Name = fi.Name, Path = fi.FullName, IsDirectory = (fi.Attributes & FileAttributes.Directory) != 0 }));
                    }
                }
                catch { }
            });
        }

        private void LocalList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(LocalList, e.OriginalSource as DependencyObject) as ListViewItem;
            if (item == null) return;
            var entry = item.Content as FileEntry;
            if (entry == null) return;
            DragDrop.DoDragDrop(LocalList, entry.Path, DragDropEffects.Copy);
        }

        private void RemoteList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(RemoteList, e.OriginalSource as DependencyObject) as ListViewItem;
            if (item == null) return;
            var entry = item.Content as FileEntry;
            if (entry == null) return;
            // For remote->local download we pass remote path string
            DragDrop.DoDragDrop(RemoteList, entry.Path, DragDropEffects.Copy);
        }

        private void RemoteList_DragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        private void LocalList_DragOver(object sender, DragEventArgs e) { e.Effects = DragDropEffects.Copy; e.Handled = true; }

        private async void RemoteList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.StringFormat)) return;
            var path = e.Data.GetData(DataFormats.StringFormat) as string;
            if (string.IsNullOrEmpty(path)) return;
            // assume local -> remote (upload)
            if (File.Exists(path))
            {
                if (Sftp != null)
                {
                    await Task.Run(() => Sftp.UploadFile(path, _remotePath));
                    await RefreshRemote();
                }
            }
        }

        private async void LocalList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.StringFormat)) return;
            var path = e.Data.GetData(DataFormats.StringFormat) as string;
            if (string.IsNullOrEmpty(path)) return;
            // assume remote -> local (download)
            if (Sftp != null)
            {
                var target = System.IO.Path.Combine(_localPath, System.IO.Path.GetFileName(path));
                await Task.Run(() => Sftp.DownloadFile(path, target));
                await RefreshLocal();
            }
        }

        private Task RefreshRemote()
        {
            return Task.Run(() =>
            {
                Application.Current.Dispatcher.Invoke(() => RemoteItems.Clear());
                try
                {
                    if (Sftp == null) return;
                    var list = Sftp.ListDirectory(_remotePath);
                    foreach (var it in list)
                    {
                        Application.Current.Dispatcher.Invoke(() => RemoteItems.Add(new FileEntry { Name = it.Name, Path = it.FullName, IsDirectory = it.IsDirectory }));
                    }
                }
                catch { }
            });
        }
    }
}
