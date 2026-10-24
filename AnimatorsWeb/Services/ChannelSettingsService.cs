using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ChannelSettingsService
    {
        private readonly AppDbContext _db;

        public ChannelSettingsService(AppDbContext db) { _db = db; }

        public async Task<ChannelSettings> GetAsync()
        {
            var settings = await _db.ChannelSettings.OrderBy(c => c.Id).FirstOrDefaultAsync();
            return settings ?? new ChannelSettings(); // не сохраняем в БД
        }

        public async Task SaveAsync(ChannelSettings settings)
        {
            if (settings.Id == 0)
                _db.ChannelSettings.Add(settings);
            else
                _db.ChannelSettings.Update(settings);

            await _db.SaveChangesAsync();
        }
    }
}