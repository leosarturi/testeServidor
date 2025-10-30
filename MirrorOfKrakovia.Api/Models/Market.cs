using MongoDB.Bson.Serialization.Attributes;

namespace MirrorOfKrakovia.Api.Models
{
    public class Market
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [BsonElement("id_item")]
        public string IdItem { get; set; } = string.Empty;

        [BsonElement("id_vendedor")]
        public string IdVendedor { get; set; } = string.Empty;

        [BsonElement("preco")]
        public double Preco { get; set; } = 0;

        [BsonElement("quantidade")]
        public int Quantidade { get; set; } = 1;

        [BsonElement("data_publicacao")]
        public DateTime DataPublicacao { get; set; } = DateTime.UtcNow;
    }
}
