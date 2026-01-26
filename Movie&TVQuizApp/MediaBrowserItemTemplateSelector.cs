using System.Windows;
using System.Windows.Controls;

namespace Movie_AnimeQuizApp {
    public class MediaBrowserItemTemplateSelector : DataTemplateSelector {
        public DataTemplate MediaTemplate { get; set; }
        public DataTemplate LoadMoreTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) {
            var row = item as MediaBrowser.BrowserRow;
            if (row != null && row.IsLoadMore) return LoadMoreTemplate;
            return MediaTemplate;
        }
    }
}