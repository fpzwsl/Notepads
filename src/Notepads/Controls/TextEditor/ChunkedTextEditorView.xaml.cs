namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Threading.Tasks;
    using Windows.System;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Input;
    using Windows.UI.Xaml.Media;
    using Notepads.Utilities;

    public sealed partial class ChunkedTextEditorView : UserControl, IDisposable
    {
        private ChunkedTextDocument _document;
        private TextBox _activeTextBox;

        public event EventHandler TextChanging;
        public event EventHandler SelectionChanged;

        public ObservableCollection<ChunkItem> Items { get; } = new ObservableCollection<ChunkItem>();

        public ChunkedTextEditorView()
        {
            InitializeComponent();
            ChunkList.ItemsSource = Items;
        }

        public async Task InitializeAsync(ChunkedTextDocument document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _activeTextBox = null;
            Items.Clear();
            for (var index = 0; index < document.ChunkCount; index++)
            {
                Items.Add(new ChunkItem(index));
            }

            if (Items.Count > 0)
            {
                Items[0].Text = await document.GetChunkAsync(0);
                Items[0].IsLoaded = true;
            }
        }

        public async Task MoveToChunkAsync(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            await EnsureChunkLoadedAsync(index);
            ChunkList.ScrollIntoView(Items[index]);
            if (ChunkList.ContainerFromItem(Items[index]) is ListViewItem container)
            {
                var textBox = FindTextBox(container);
                textBox?.Focus(FocusState.Programmatic);
            }
        }

        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_activeTextBox != null)
            {
                var start = _activeTextBox.SelectionStart;
                _activeTextBox.Text = _activeTextBox.Text.Insert(start, text);
                _activeTextBox.SelectionStart = start + text.Length;
            }
        }

        public void FocusEditor()
        {
            _activeTextBox?.Focus(FocusState.Programmatic);
        }

        public void Dispose()
        {
            _document = null;
            _activeTextBox = null;
            Items.Clear();
        }

        private async void ChunkList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || !(args.Item is ChunkItem item) || item.IsLoaded) return;
            await EnsureChunkLoadedAsync(item.Index);
        }

        private async Task EnsureChunkLoadedAsync(int index)
        {
            var item = Items[index];
            if (item.IsLoaded || _document == null) return;
            item.Text = await _document.GetChunkAsync(index);
            item.IsLoaded = true;
        }

        private async void ChunkTextBox_TextChanged(object sender, TextChangedEventArgs args)
        {
            if (!(sender is TextBox textBox) || !(textBox.DataContext is ChunkItem item) || !item.IsLoaded || _document == null) return;
            if (item.Text == textBox.Text) return;
            item.Text = textBox.Text;
            await _document.ReplaceChunkAsync(item.Index, item.Text);
            TextChanging?.Invoke(this, EventArgs.Empty);
        }

        private void ChunkTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (!(sender is TextBox textBox) || !(textBox.DataContext is ChunkItem item)) return;
            if (args.Key == VirtualKey.PageDown &&
                Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                args.Handled = true;
                _ = MoveToChunkAsync(item.Index + 1);
            }
            else if (args.Key == VirtualKey.PageUp &&
                     Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                args.Handled = true;
                _ = MoveToChunkAsync(item.Index - 1);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ChunkTextBox_GotFocus(object sender, RoutedEventArgs args)
        {
            _activeTextBox = sender as TextBox;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private static TextBox FindTextBox(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is TextBox textBox) return textBox;
                if (FindTextBox(child) is TextBox nested) return nested;
            }
            return null;
        }

        public sealed class ChunkItem : INotifyPropertyChanged
        {
            private string _text = string.Empty;

            public ChunkItem(int index) { Index = index; }

            public int Index { get; }

            public bool IsLoaded { get; set; }

            public string Text
            {
                get => _text;
                set
                {
                    if (_text == value) return;
                    _text = value ?? string.Empty;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
