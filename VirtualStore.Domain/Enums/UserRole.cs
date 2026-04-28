namespace VirtualStore.Domain.Enums;

[Flags]
public enum UserRole
{
    Customer = 1,
    Manager = 2,
    Admin = 4
}