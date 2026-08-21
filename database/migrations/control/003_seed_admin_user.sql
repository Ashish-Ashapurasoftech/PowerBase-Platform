-- Seed the default SuperAdmin user
IF NOT EXISTS (SELECT 1 FROM core.[User] WHERE Email = 'admin@mail.com')
BEGIN
    INSERT INTO core.[User] (
        PublicId, 
        Email, 
        EmailNormalized, 
        HashedPassword, 
        Name, 
        IsEmailVerified, 
        IsActive, 
        IsDeleted, 
        SystemRoleId
    ) 
    VALUES (
        '36fb5633-3d54-f111-ae78-38689390cafd', 
        'admin@mail.com', 
        'ADMIN@MAIL.COM', 
        '$2a$11$Y1gKET9z2XVfZP5bOj20CeYThx9tQ7LPWLWZaEhQxwoMDWq7NznTG', 
        'Parth Sheth', 
        0, 
        1, 
        0, 
        1
    )
END
GO
