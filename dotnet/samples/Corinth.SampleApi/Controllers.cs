using Microsoft.AspNetCore.Mvc;

namespace Corinth.SampleApi;

/// <summary>Product endpoints served through MVC: JSON by default, CCF for <c>Accept: application/vnd.ccf</c>.</summary>
[ApiController]
public sealed class ProductsController : ControllerBase
{
    private readonly SampleData _data;

    /// <summary>Creates the controller.</summary>
    public ProductsController(SampleData data)
    {
        _data = data;
    }

    /// <summary>All 500 products.</summary>
    [HttpGet("/products")]
    public ActionResult<List<Product>> GetAll() => _data.Products.ToList();

    /// <summary>One product, as a single-object CCF response.</summary>
    [HttpGet("/products/{id:long}")]
    public ActionResult<Product> Get(long id) =>
        _data.Products.FirstOrDefault(p => p.Id == id) is { } product ? product : NotFound();

    /// <summary>An empty list.</summary>
    [HttpGet("/products/empty")]
    public ActionResult<List<Product>> GetEmpty() => new List<Product>();
}

/// <summary>Other sample endpoints.</summary>
[ApiController]
public sealed class MoreController : ControllerBase
{
    private readonly SampleData _data;
    private readonly EnumerationCounter _counter;

    /// <summary>Creates the controller.</summary>
    public MoreController(SampleData data, EnumerationCounter counter)
    {
        _data = data;
        _counter = counter;
    }

    /// <summary>Orders: nested object, list of objects, list of strings, nulls.</summary>
    [HttpGet("/orders")]
    public ActionResult<List<Order>> GetOrders() => _data.Orders.ToList();

    /// <summary>Every scalar type.</summary>
    [HttpGet("/all-types")]
    public ActionResult<List<AllTypes>> GetAllTypes() => _data.AllTypes.ToList();

    /// <summary>A type with no CCF schema: always JSON.</summary>
    [HttpGet("/unsupported")]
    public ActionResult<UnsupportedReport> GetUnsupported() => new UnsupportedReport { Title = "report", Details = new { a = 1 } };

    /// <summary>Data CCF cannot encode: falls back to JSON.</summary>
    [HttpGet("/bad-decimal")]
    public ActionResult<List<PricedItem>> GetBadDecimal() => new List<PricedItem> { new() { Name = "x", Price = 1.234m } };

    /// <summary>A scalar: always JSON.</summary>
    [HttpGet("/scalar-value")]
    public ActionResult<int> GetScalar() => 42;

    /// <summary>An IActionResult whose value is a LINQ iterator: served through IEnumerable&lt;T&gt;.</summary>
    [HttpGet("/lazy-products")]
    [ProducesResponseType<List<Product>>(StatusCodes.Status200OK)]
    public IActionResult GetLazy() => Ok(_data.Products.Where(p => p.Stock > 500).Select(p => p));

    /// <summary>A lazily evaluated sequence that counts how often it is enumerated.</summary>
    [HttpGet("/counted")]
    [ProducesResponseType<List<Product>>(StatusCodes.Status200OK)]
    public IActionResult GetCounted() => Ok(new CountingEnumerable<Product>(_data.Products.Take(5), _counter));

    /// <summary>A counted sequence whose data CCF cannot encode: the JSON fallback must not enumerate it again.</summary>
    [HttpGet("/counted-bad")]
    [ProducesResponseType<List<PricedItem>>(StatusCodes.Status200OK)]
    public IActionResult GetCountedBad() =>
        Ok(new CountingEnumerable<PricedItem>(new[] { new PricedItem { Name = "x", Price = 1.234m } }, _counter));

    /// <summary>[Produces] restricts the offered media types: CCF is never offered here.</summary>
    [HttpGet("/json-only")]
    [Produces("application/json")]
    public ActionResult<List<Product>> GetJsonOnly() => _data.Products.Take(3).ToList();

    /// <summary>A null result.</summary>
    [HttpGet("/null")]
    public IActionResult GetNull() => Ok(null);
}
