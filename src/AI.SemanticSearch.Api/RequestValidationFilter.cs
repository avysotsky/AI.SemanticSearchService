using System.ComponentModel.DataAnnotations;

namespace AI.SemanticSearch.Api;

public sealed class RequestValidationFilter<T> : IEndpointFilter where T : class
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<T>().FirstOrDefault();
        if (model is null) return next(context);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model, new ValidationContext(model), results, true)) return next(context);
        var errors = results.SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
            .Select(member => (member, message: result.ErrorMessage ?? "Invalid value.")))
            .GroupBy(item => item.member).ToDictionary(group => group.Key, group => group.Select(item => item.message).ToArray());
        return ValueTask.FromResult<object?>(Results.ValidationProblem(errors));
    }
}
