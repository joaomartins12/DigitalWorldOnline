namespace DigitalWorldOnline.Commons.DTOs.Config
{
    public class GlobalDropsConfigDTO
    {
        public int Id { get; private set; }
        public int ItemId { get; private set; }
        public byte MinDrop { get; private set; }
        public byte MaxDrop { get; private set; }
        public double Chance { get; private set; }
        public int Map { get; private set; }
        public DateTime StartTime { get; private set; }
        public DateTime EndTime { get; private set; }
    }
}