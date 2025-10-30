using MongoDB.Driver;
using dotenv.net;

namespace MirrorOfKrakovia.Api.Services
{
    public class DatabaseService
    {
        private readonly IMongoDatabase _db;

        public DatabaseService()
        {
            DotEnv.Load();
            var uri = Environment.GetEnvironmentVariable("MONGO_URI");
            var name = Environment.GetEnvironmentVariable("MONGO_DB");

            if (string.IsNullOrEmpty(uri))
                throw new Exception("MONGO_URI não encontrada!");

            var client = new MongoClient(uri);
            _db = client.GetDatabase(name);
        }

        public IMongoCollection<T> GetCollection<T>(string collection) =>
            _db.GetCollection<T>(collection);
    }
}
