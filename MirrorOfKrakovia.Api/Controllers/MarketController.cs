using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MirrorOfKrakovia.Api.Models;
using MirrorOfKrakovia.Api.Services;

namespace MirrorOfKrakovia.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MarketController : ControllerBase
    {
        private readonly IMongoCollection<Market> _market;

        public MarketController(DatabaseService db)
        {
            _market = db.GetCollection<Market>("market");
        }

        [HttpGet]
        public async Task<IActionResult> Get() =>
            Ok(await _market.Find(_ => true).ToListAsync());

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var marketItem = await _market.Find(m => m.Id == id).FirstOrDefaultAsync();
            return marketItem == null ? NotFound() : Ok(marketItem);
        }

        // 🔗 lista itens de um vendedor específico
        [HttpGet("vendedor/{idVendedor}")]
        public async Task<IActionResult> GetByVendedor(string idVendedor)
        {
            var list = await _market.Find(m => m.IdVendedor == idVendedor).ToListAsync();
            return Ok(list);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Market market)
        {
            await _market.InsertOneAsync(market);
            return CreatedAtAction(nameof(GetById), new { id = market.Id }, market);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Market market)
        {
            var result = await _market.ReplaceOneAsync(m => m.Id == id, market);
            return result.MatchedCount == 0 ? NotFound() : Ok(market);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _market.DeleteOneAsync(m => m.Id == id);
            return result.DeletedCount == 0 ? NotFound() : NoContent();
        }
    }
}
