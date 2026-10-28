using WinQEMU.Core.Services;

namespace WinQEMU.Core.Tests;

/// <summary>SHA-512 crypt, checked against the vectors of Ulrich Drepper's specification.</summary>
public class Sha512CryptTests
{
    [Theory]
    [InlineData("Hello world!", "saltstring", "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    // "$6$rounds=5000$toolongsaltstring" in the specification: 5000 is the default, the salt is cut at 16.
    [InlineData("This is just a test", "toolongsaltstring",
        "$6$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0")]
    public void Matches_the_reference_vectors(string password, string salt, string expected)
    {
        Assert.Equal(expected, Sha512Crypt.Hash(password, salt));
    }

    [Fact]
    public void A_password_longer_than_a_digest_is_hashed_as_the_specification_says()
    {
        Assert.Equal(
            "$6$rounds=1400$anotherlongsalts$POfYwTEok97VWcjxIiSOjiykti.o/pQs.wPvMxQ6Fm7I6IoYN3CmLs66x9t0oSwbtEW7o7UmJEiDwGqd8p4ur1",
            Sha512Crypt.Hash("a very much longer text to encrypt.  This one even stretches over morethan one line.", "anotherlongsaltstring", 1400));
    }

    [Fact]
    public void Each_hash_gets_its_own_salt()
    {
        var first = Sha512Crypt.Hash("ubuntu");
        var second = Sha512Crypt.Hash("ubuntu");

        Assert.StartsWith("$6$", first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, Sha512Crypt.Hash("ubuntu", first.Split('$')[2]));
    }
}
