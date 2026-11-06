namespace Coo.Api.Hosting;

/// <summary>
/// The web page Google Play's Data safety form links to for deleting an account (Play's account deletion policy):
/// how to delete it in the app, how to ask by email without the app, and what goes.
/// </summary>
public static class DeleteAccountPage
{
    public static void MapDeleteAccountPage(this IEndpointRouteBuilder app) =>
        app.MapGet("/delete-account", () => Results.Content(Html, "text/html; charset=utf-8"));

    internal const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Delete your Mochi account</title>
        <style>
        body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: #EEEEE6; color: #262A22; margin: 0; padding: 32px 20px; line-height: 1.5; }
        main { max-width: 34rem; margin: 0 auto; }
        h1 { font-size: 1.5rem; margin: 0 0 12px; }
        h2 { font-size: 1.1rem; margin: 24px 0 8px; }
        li { margin: 8px 0; }
        a { color: #3D5E2B; }
        footer { margin-top: 32px; font-size: 0.9rem; color: #62685A; }
        </style>
        </head>
        <body>
        <main>
        <h1>Delete your Mochi account</h1>
        <p>Mochi — Baby Log &amp; Sleep is made by Lech Digital. You can delete your account and its data at any time.</p>
        <h2>In the app</h2>
        <ol>
        <li>Open Mochi and go to <strong>Settings → Account → Delete account</strong>.</li>
        <li>Confirm. It happens at once, on our server and on the phone.</li>
        </ol>
        <h2>Without the app</h2>
        <p>Email <a href="mailto:andrzej@lechdigital.nl?subject=Delete%20my%20Coo%20account">andrzej@lechdigital.nl</a>
        from the Google account you sign in to Mochi with, with the subject “Delete my Mochi account”. We delete it within 30
        days and confirm by email.</p>
        <h2>What is deleted</h2>
        <ul>
        <li>Your account: your name, email address and Google account link, and every signed-in device.</li>
        <li>Your place in each family. A family you are the only member of is deleted with its whole baby log
        (feeds, sleep, diapers, pumping), its baby profiles and avatars, and its Advisor usage.</li>
        <li>A family that has other members stays with them, with the entries everyone logged, and its ownership passes on.</li>
        </ul>
        <h2>What is kept</h2>
        <ul>
        <li>Nothing on our server about you, except in database backups, which are deleted after 14 days.</li>
        <li>A Premium subscription is billed by Google Play: cancel it in Google Play, as deleting the account does not.</li>
        <li>What stays on your phone until you uninstall Mochi or clear its data.</li>
        </ul>
        <footer><a href="https://lechdigital.nl/projects/mochi/privacy">Privacy policy</a> · <a href="https://lechdigital.nl">lechdigital.nl</a></footer>
        </main>
        </body>
        </html>
        """;
}
