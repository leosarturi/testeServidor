using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MirrorOfKrakovia.Api.Models;
using MirrorOfKrakovia.Api.Services;

namespace MirrorOfKrakovia.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ItemController : ControllerBase
    {
        private readonly IMongoCollection<Item> _itens;

        public ItemController(DatabaseService db)
        {
            _itens = db.GetCollection<Item>("itens");
        }

        [HttpGet]
        public async Task<IActionResult> GetAll() =>
            Ok(await _itens.Find(_ => true).ToListAsync());

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var item = await _itens.Find(i => i.Id == id).FirstOrDefaultAsync();
            return item == null ? NotFound() : Ok(item);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Item item)
        {
            await _itens.InsertOneAsync(item);
            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Item item)
        {
            var result = await _itens.ReplaceOneAsync(i => i.Id == id, item);
            return result.MatchedCount == 0 ? NotFound() : Ok(item);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var result = await _itens.DeleteOneAsync(i => i.Id == id);
            return result.DeletedCount == 0 ? NotFound() : NoContent();
        }
    }
}
