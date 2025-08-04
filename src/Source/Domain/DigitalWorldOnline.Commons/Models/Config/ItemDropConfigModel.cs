namespace DigitalWorldOnline.Commons.Models.Config
{
    public sealed partial class ItemDropConfigModel
    {
        /// <summary>
        /// Sequencial unique identifier.
        /// </summary>
        public long Id { get; private set; }

        /// <summary>
        /// Client item id information.
        /// </summary>
        public int ItemId { get; set; }

        /// <summary>
        /// Item name.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Min. amount of the item.
        /// </summary>
        public int MinAmount { get; set; }
        
        /// <summary>
        /// Max. amount of the item.
        /// </summary>
        public int MaxAmount { get; set; }

        /// <summary>
        /// Chance of drop.
        /// </summary>
        public double Chance { get; set; }

        /// <summary>
        /// Raid reward rank.
        /// </summary>
        public int Rank { get; private set; }

        /// <summary>
        /// Reference to the drop reward.
        /// </summary>
        public long DropRewardId { get; private set; }

        public ItemDropConfigModel()
        {
            ItemId = 90101;
            Chance = 50;
            MinAmount = 1;
            MaxAmount = 1;
        }
    }
}