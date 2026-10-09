namespace ContentManagement.Server.Domain;

public enum ContentStatus
{
    Uploading = 0,
    Active = 1,
    MarkedForDeletion = 2,
    Deleting = 3,
    Deleted = 4
}
