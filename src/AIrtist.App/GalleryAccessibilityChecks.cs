using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AIrtist.App;

internal static class GalleryAccessibilityChecks
{
    internal static void Verify(ItemsControl gallery, Func<object, string> expectedName)
    {
        gallery.Dispatcher.VerifyAccess();
        var owner = new GalleryPeer(gallery);
        foreach (object item in gallery.Items)
        {
            // Exercise WPF's item-name fallback even without an attached accessibility client.
            // Rendering alone does not call this path on every machine.
            string name = new ArtworkPeer(item, owner).GetName();
            OnlineSmoke.Require(name == expectedName(item), "Gallery accessibility name differs from its player label");
            OnlineSmoke.Require(item.ToString() == name, "Gallery text inspected image/decoder members");
        }
    }

    private sealed class GalleryPeer(ItemsControl gallery) : ItemsControlAutomationPeer(gallery)
    {
        protected override string GetClassNameCore() => "Gallery";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new ArtworkPeer(item, this);
    }

    private sealed class ArtworkPeer(object item, ItemsControlAutomationPeer owner) : ItemAutomationPeer(item, owner)
    {
        protected override string GetClassNameCore() => "Artwork";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    }
}
