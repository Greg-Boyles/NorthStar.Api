using NorthStar.Api.Models;

namespace NorthStar.Api.Interfaces;

public interface ICarService
{
    Task<List<Car>> GetCarsAsync(string accessToken);
}
