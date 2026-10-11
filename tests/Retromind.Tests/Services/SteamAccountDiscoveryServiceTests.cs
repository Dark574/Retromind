using Retromind.Services.SteamAchievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class SteamAccountDiscoveryServiceTests
{
    [Fact]
    public void Parse_ReturnsAccountsWithMostRecentAccountFirst()
    {
        const string content =
            """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName"      "first_account"
                    "PersonaName"      "First player"
                    "MostRecent"       "0"
                }
                "76561198000000002"
                {
                    "AccountName"      "second_account"
                    "PersonaName"      "Second player"
                    "MostRecent"       "1"
                }
            }
            """;

        var accounts = SteamAccountDiscoveryService.Parse(content);

        Assert.Equal(2, accounts.Count);

        Assert.Equal("76561198000000002", accounts[0].SteamId64);
        Assert.Equal("second_account", accounts[0].AccountName);
        Assert.Equal("Second player", accounts[0].PersonaName);
        Assert.True(accounts[0].IsMostRecent);

        Assert.Equal("76561198000000001", accounts[1].SteamId64);
        Assert.False(accounts[1].IsMostRecent);
    }

    [Fact]
    public async Task DiscoverAsync_ReadsExistingCandidateFile()
    {
        using var temp = new TemporaryDirectory();

        var loginUsersPath = temp.CreateFile(
            "Steam/config/loginusers.vdf",
            """
            "users"
            {
                "76561198000000042"
                {
                    "AccountName" "test_account"
                    "PersonaName" "Test player"
                    "MostRecent" "1"
                }
            }
            """);

        var missingPath = temp.GetPath("missing", "loginusers.vdf");
        var service = new SteamAccountDiscoveryService(
            [missingPath, loginUsersPath]);

        var account = Assert.Single(await service.DiscoverAsync());

        Assert.Equal("76561198000000042", account.SteamId64);
        Assert.Equal("test_account", account.AccountName);
        Assert.Equal("Test player", account.PersonaName);
        Assert.True(account.IsMostRecent);
    }

    [Fact]
    public void Parse_IgnoresMalformedAccountIds()
    {
        const string content =
            """
            "users"
            {
                "not-a-steam-id"
                {
                    "AccountName" "invalid"
                    "PersonaName" "Invalid"
                    "MostRecent" "1"
                }
            }
            """;

        Assert.Empty(SteamAccountDiscoveryService.Parse(content));
    }

    [Fact]
    public void Parse_ReturnsEmptyListForEmptyContent()
    {
        Assert.Empty(SteamAccountDiscoveryService.Parse(string.Empty));
    }
}