using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Corinth.AspNetCore.Tests;

/// <summary>CCF mirrors how each path's own JSON options write enums (global converter or none).</summary>
public class EnumJsonMirrorTests
{
    private static readonly Action<IServiceCollection> NumericEnums = services =>
    {
        // The sample adds JsonStringEnumConverter to both paths; remove it so enums are written as numbers.
        services.PostConfigure<JsonOptions>(o => o.JsonSerializerOptions.Converters.Clear());
        services.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.Converters.Clear());
    };

    private static async Task<(CcfSchema Schema, object?[] Rows, JsonElement Json)> FetchBothAsync(TestHost host, string path)
    {
        var (_, body) = await host.GetBytesAsync(path, "application/vnd.ccf; features=d");
        var schemaText = await (await host.GetAsync("/ccf/schemas/" + CcfDecoder.PeekSchemaId(body))).Content.ReadAsStringAsync();
        var schema = CcfSchema.Parse(schemaText);
        var rows = (object?[])CcfDecoder.DecodeValue(body, schema);
        var json = JsonDocument.Parse(await host.Client.GetStringAsync(path)).RootElement.Clone();
        return (schema, rows, json);
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/min/products")]
    public async Task Enums_the_json_options_write_as_numbers_are_integer_columns_with_the_same_numbers(string path)
    {
        await using var host = await TestHost.StartAsync(services: NumericEnums);
        var (schema, rows, json) = await FetchBothAsync(host, path);

        Assert.Contains("\"name\":\"status\",\"type\":\"int32\"", schema.CanonicalJson);
        Assert.Equal(JsonValueKind.Number, json[0].GetProperty("status").ValueKind);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(json[i].GetProperty("status").GetInt32(), Convert.ToInt32(((CcfObject)rows[i]!)["status"]));
        }
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/min/products")]
    public async Task Enums_the_json_options_write_as_strings_are_enum_columns_with_the_same_names(string path)
    {
        await using var host = await TestHost.StartAsync();
        var (schema, rows, json) = await FetchBothAsync(host, path);

        Assert.Contains("\"name\":\"status\",\"type\":\"enum\"", schema.CanonicalJson);
        Assert.Equal(JsonValueKind.String, json[0].GetProperty("status").ValueKind);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(json[i].GetProperty("status").GetString(), (string?)((CcfObject)rows[i]!)["status"]);
        }
    }

    [Fact]
    public async Task Each_path_follows_its_own_json_options()
    {
        // Strings on the MVC path, numbers on the minimal-API path.
        await using var host = await TestHost.StartAsync(services: s =>
            s.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.Converters.Clear()));
        var mvc = await FetchBothAsync(host, "/products");
        var http = await FetchBothAsync(host, "/min/products");

        Assert.Contains("\"type\":\"enum\"", mvc.Schema.CanonicalJson);
        Assert.Contains("\"name\":\"status\",\"type\":\"int32\"", http.Schema.CanonicalJson);
        Assert.NotEqual(mvc.Schema.IdHex, http.Schema.IdHex);
    }
}
