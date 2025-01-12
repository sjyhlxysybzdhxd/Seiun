using System.Net.Mime;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Seiun.Entities;
using Seiun.Utils;

namespace Seiun.Repositories;

public class UserRepository(SeiunDbContext dbContext, IMinioClient minioClient)
    : BaseRepository<UserEntity>(dbContext, minioClient), IUserRepository
{
    public async Task<UserEntity?> GetByPhoneNumberAsync(string phoneNumber)
    {
        return await DbContext.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber);
    }

    public async Task<UserEntity?> GetByUserNameAsync(string userName)
    {
        return await DbContext.Users.FirstOrDefaultAsync(u => u.UserName == userName);
    }

    public async Task<UserEntity?> GetByEmailAsync(string email)
    {
        return await DbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task UpdateAvatarAsync(UserEntity user, Stream avatarData)
    {
        if (user.AvatarFileName != null)
        {
            // Delete old avatars
            var removeObjectArgs = new RemoveObjectArgs().WithBucket(Constants.BucketNames.Avatar)
                .WithObject(user.AvatarFileName);
            await MinioCl.RemoveObjectAsync(removeObjectArgs).ConfigureAwait(false);
        }
        
        // Upload new avatar
        var avatarName = $"{Guid.NewGuid()}.webp";
        var putObjectArgs = new PutObjectArgs()
            .WithBucket(Constants.BucketNames.Avatar)
            .WithObject(avatarName)
            .WithContentType(MediaTypeNames.Image.Webp)
            .WithStreamData(avatarData)
            .WithObjectSize(avatarData.Length);
        await MinioCl.PutObjectAsync(putObjectArgs);
        
        // Update user avatar file name
        user.AvatarFileName = avatarName;
        DbContext.Users.Update(user);
        await DbContext.SaveChangesAsync();
    }
}