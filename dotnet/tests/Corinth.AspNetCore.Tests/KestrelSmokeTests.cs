using Xunit;

namespace Corinth.AspNetCore.Tests;

/// <summary>One test over a real TCP socket: the bytes that reach an HttpClient through Kestrel are the vector bytes.</summary>
public class KestrelSmokeTests
{
    [Fact]
    public async Task Plain_dictionary_and_json_responses_survive_a_real_connection()
    {
        await using var host = await SampleHost.StartAsync();

        var (plain, plainBody) = await host.GetBytesAsync("/products", "application/vnd.ccf");
        Assert.Equal("application/vnd.ccf", plain.ContentType());
        Assert.Equal(Vectors.Bytes("v01-products-plain"), plainBody);
        Assert.Equal(plainBody.Length, plain.Content.Headers.ContentLength);

        var (dict, dictBody) = await host.GetBytesAsync("/products", "application/vnd.ccf; features=d, application/json;q=0.5");
        Assert.Equal("application/vnd.ccf; features=d", dict.ContentType());
        Assert.Equal(Vectors.Bytes("v02-products-dict"), dictBody);

        var json = await host.GetAsync("/products");
        Assert.StartsWith("application/json", json.ContentType());
        Assert.Contains("Accept", json.Vary());

        var schema = await host.GetAsync("/ccf/schemas/c33a3ed0");
        Assert.Equal(Vectors.SchemaText("schema-Product.json"), await schema.Content.ReadAsStringAsync());
    }
}
