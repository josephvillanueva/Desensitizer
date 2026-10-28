using System;

namespace Desensitizer;

// Connects the open project's review preferences to the detectors. The interface calls Bind when a
// project is unlocked and Unbind when it is closed, so one project's keep-as-is labels and generic
// terms never apply to another project's documents.
//
//   ProjectScope.Bind(project);   // after ProjectStore.Load
//   ProjectScope.Unbind();        // on close or "back to projects"
//   project.ReviewPreferences.AddKeepAsIs(...) / AddGenericTerm(...)  // edits take effect at once
//   ProjectStore.Save(path, project)                                   // persist them
//   DocumentPrivacyPolicy.SuppressedOrganizations                      // what the lists hid, for the user to check
public static class ProjectScope
{
    private static readonly object Gate = new();
    private static ProjectReviewPreferences? bound;

    public static bool IsBound { get { lock (Gate) return bound != null; } }

    public static void Bind(Project project)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        lock (Gate)
        {
            if (bound != null) bound.Changed -= Refresh;
            bound = project.ReviewPreferences;
            bound.Changed += Refresh;
            ReviewAllowList.Bind(bound);
            DocumentPrivacyPolicy.SetProjectTerms(bound.GenericTermsSnapshot());
            DocumentPrivacyPolicy.ClearSuppressed();
        }
    }

    public static void Unbind()
    {
        lock (Gate)
        {
            if (bound != null) bound.Changed -= Refresh;
            bound = null;
            ReviewAllowList.Unbind();
            DocumentPrivacyPolicy.ClearProjectTerms();
            DocumentPrivacyPolicy.ClearSuppressed();
        }
    }

    // Re-reads the generic terms. Called automatically after edits made through ProjectReviewPreferences.
    public static void Refresh()
    {
        lock (Gate)
        {
            if (bound != null) DocumentPrivacyPolicy.SetProjectTerms(bound.GenericTermsSnapshot());
        }
    }
}
