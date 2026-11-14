using System.Net;
using Everywhere.Common;
using Everywhere.Extensions;

namespace Everywhere.Core.Tests.Common;

public sealed class HandledExceptionTests
{
    [Test]
    public void Handle_WhenHttp500IsReturned_PreservesResponseStatusInsteadOfReportingSocketFailure()
    {
        var original = new HttpRequestException("Response status code does not indicate success: 500.", null, HttpStatusCode.InternalServerError);
        var handled = HandledSystemException.Handle(original);
        Assert.That(handled, Is.TypeOf<HandledHttpException>());
        var response = (HandledHttpException)handled;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(response.InnerException, Is.SameAs(original));
            Assert.That(original.GetFriendlyMessage().ToString(), Is.EqualTo(response.FriendlyMessageKey.ToString()));
        }
    }
}