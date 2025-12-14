using System;
using System.Linq.Expressions;
using System.Reflection;

namespace FlexLabs.EntityFrameworkCore.Upsert;

/// <summary>
/// Represents a single setter used by the setter-style WhenMatched() API.
/// </summary>
public sealed record UpsertSetter(MemberInfo Member, LambdaExpression ValueExpression);
