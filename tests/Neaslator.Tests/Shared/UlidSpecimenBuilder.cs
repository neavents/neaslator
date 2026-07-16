using System.Reflection;
using AutoFixture.Kernel;

namespace Neaslator.Tests.Shared;

public sealed class UlidSpecimenBuilder : ISpecimenBuilder
{
    public object Create(object request, ISpecimenContext context)
    {
        if (request is PropertyInfo pi &&
            pi.PropertyType == typeof(string) &&
            (pi.Name.EndsWith("Id", StringComparison.Ordinal) ||
             pi.Name == "Id" ||
             pi.Name.EndsWith("Code", StringComparison.Ordinal) ||
             pi.Name == "Code"))
        {
            return Ulid.NewUlid().ToString();
        }

        if (request is ParameterInfo paramInfo &&
            paramInfo.ParameterType == typeof(string) &&
            (paramInfo.Name != null &&
             (paramInfo.Name.EndsWith("Id", StringComparison.Ordinal) ||
              paramInfo.Name == "id")))
        {
            return Ulid.NewUlid().ToString();
        }

        return new NoSpecimen();
    }
}
