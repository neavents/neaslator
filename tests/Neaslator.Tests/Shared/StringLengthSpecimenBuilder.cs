using System.Reflection;
using AutoFixture.Kernel;

namespace Neaslator.Tests.Shared;

public sealed class StringLengthSpecimenBuilder : ISpecimenBuilder
{
    private readonly int _maxLength;

    public StringLengthSpecimenBuilder(int maxLength = 200)
    {
        _maxLength = maxLength;
    }

    public object Create(object request, ISpecimenContext context)
    {
        if (request is PropertyInfo pi && pi.PropertyType == typeof(string))
        {
            var faker = new Bogus.Faker();
            return faker.Lorem.Letter(_maxLength);
        }

        return new NoSpecimen();
    }
}
