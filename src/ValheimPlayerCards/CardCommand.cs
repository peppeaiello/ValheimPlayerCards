using Jotunn.Entities;

namespace ValheimPlayerCards
{
    /// <summary>Console command "playercard": take a new portrait and send the card now.</summary>
    internal sealed class CardCommand : ConsoleCommand
    {
        public override string Name => "playercard";

        public override string Help => "Take a new portrait of your character and send your player card to the server now.";

        public override void Run(string[] args)
        {
            ClientSide.ForceNext();
            Console.instance?.Print("ValheimPlayerCards: your card will be sent in a moment (the server accepts one every few seconds).");
        }
    }
}
