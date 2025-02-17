using Seiun.Services;
using Microsoft.AspNetCore.Mvc;
using Seiun.Utils.Enums;
using Seiun.Resources;
using Seiun.Entities;
using Seiun.Models.Responses;
using Seiun.Models.Parameters;
using SixLabors.ImageSharp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Seiun.Utils;
using Nest;

namespace Seiun.Controllers;
/// <summary>
/// 文章控制器
/// </summary>
/// <param name="logger">日志</param>
/// <param name="repository">仓库服务</param>
[ApiController,Route("/api/article")]
public class ArticleController(ILogger<ArticleController> logger, IRepositoryService repository, IElasticClient elasticClient, IArticleSearchService articleSearch) : ControllerBase{
	
	/// <summary>
	/// 上传文章
	/// </summary>
	/// <param name="postCreate">文章信息DTO</param>
	/// <returns>上传结果</returns>
	[HttpPost("create", Name = "CreateArticle")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> Create([FromBody] ArticleCreate articleCreate)
	{
		var userId = User.GetUserId();
		if(userId == null)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Any.InvalidJwtToken
			));
		}

		var user = await repository.UserRepository.GetByIdAsync(userId.Value);
        if (user == null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
                StatusCodes.Status403Forbidden,
                ErrorMessages.Controller.User.UserNotFound
            ));
        }

		var article = new ArticleEntity
		{
			Article = articleCreate.Article,
			ImageFileNames = articleCreate.ImageNames,
			CreatorId = userId.Value,
			CreateTime = DateTime.UtcNow,
			IsPinned = false
		};

		repository.ArticleRepository.Create(article);
		if(await repository.ArticleRepository.SaveAsync())
		{
			var articleSearch = new ArticleSearchEntity
			{
				Article = article.Article,
				CreatorUserName = user.UserName,
				CreatorNickName = user.NickName,
				CreateTime = article.CreateTime,
				ArticleId = article.Id
			};

			var indexResponse = await elasticClient.IndexAsync(articleSearch, i => i
				.Id(articleSearch.ArticleId.ToString()));
			if(indexResponse.IsValid)
			{
				return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.CreateSuccess));
			}
			logger.LogError("Index article {} failed", article.Id);
		}

		logger.LogError("User {} Create article failed", userId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
            StatusCodes.Status500InternalServerError,
            ErrorMessages.Controller.Article.CreateFailed
		));
	}

	/// <summary>
	/// 上传文章图片
	/// </summary>
	/// <param name="articleImgFiles">文章图片文件</param>
	/// <returns>图片名称</returns>
	[HttpPost("upload-img", Name = "UploadArticleImg")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(ArticleImgNameListResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status400BadRequest)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> UploadArticleImg([FromForm] List<IFormFile>? articleImgFiles)
	{
		if(articleImgFiles == null || articleImgFiles.Count == 0)
		{
			return BadRequest(ResponseFactory.NewFailedBaseResponse(
                StatusCodes.Status400BadRequest,
                ErrorMessages.Controller.Any.FileNotUploaded
            ));
		}

		var userId = User.GetUserId();
		if(userId == null)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Any.InvalidJwtToken
			));
		}

		List<string> articleImgNames = [];

		foreach(var articleimgFile in articleImgFiles)
		{	
			if(articleimgFile.Length > Constants.Article.MaxArticleImageSize)
			{
				return BadRequest(ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status400BadRequest,
					ErrorMessages.Controller.Any.FileTooLarge
				));
			}

			var fileExtension = Path.GetExtension(articleimgFile.FileName).ToLower();
			if(!Constants.Article.AllowedArticleImageExtensions.Contains(fileExtension))
			{
				return BadRequest(ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status400BadRequest,
					ErrorMessages.Controller.Any.FileFormatNotSupported
				));
			}

			await using var articleimgStream = articleimgFile.OpenReadStream();
			Image image;
			try
			{
				image = await Image.LoadAsync(articleimgStream);
			}
			catch
			{
				return BadRequest(ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status400BadRequest,
					ErrorMessages.Controller.Any.FileFormatNotSupported
				));
			}

			if (image.Width > Constants.Article.ArticleImageMaxWidth || image.Height > Constants.Article.ArticleImageMaxHeight)
			{
				return BadRequest(ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status400BadRequest,
					ErrorMessages.Controller.Any.ImageSizeTooLarge
				));
			}

			try
			{
				await using var processedImageStream = new MemoryStream();
				await articleimgFile.CopyToAsync(processedImageStream);
				processedImageStream.Seek(0, SeekOrigin.Begin);
			    var articleImgName = await repository.ArticleRepository.UploadArticleImgAsync(processedImageStream, fileExtension);
			    articleImgNames.Add(articleImgName);
			}
			catch (Exception e)
			{
				logger.LogError(e, "Fail to upload article image");
				return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status500InternalServerError,
					ErrorMessages.Controller.Article.ArticleImgsUploadFailed
				));
			}
		}

		return Ok(ArticleImgNameListResp.Success(articleImgNames));
	}

	/// <summary>
	/// 删除文章
	/// </summary>
	/// <param name="postId">文章ID</param>
	/// <returns>删除结果</returns>
	[HttpDelete("delete/{articleId:Guid}", Name = "DeleteArticle")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status404NotFound)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> Delete(Guid articleId)
	{
		var article  = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		var userId = User.GetUserId();
		if(userId == null)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Any.InvalidJwtToken
			));
		}

		var userRole = User.GetUserRole();

		if(userRole == UserRole.Creator)
		{
			if(userId != article.CreatorId)
			{
				return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
					StatusCodes.Status403Forbidden,
					ErrorMessages.Controller.Article.UserNotAuthorized
				));
			}
		}

		var deleteResponse = await elasticClient.DeleteAsync<ArticleSearchEntity>(articleId.ToString());
		repository.ArticleRepository.Delete(article);
		if(await repository.ArticleRepository.SaveAsync()&&deleteResponse.IsValid)
		{	
			if(article.ImageFileNames != null&&await repository.ArticleRepository.DeleteAticleImgAsync(article.ImageFileNames))
			{
				return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.DeleteSuccess));
			}
			logger.LogError("User {} Delete article image {} failed", userId, articleId);
		}

		logger.LogError("User {} Delete article {} failed", userId, articleId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
            StatusCodes.Status500InternalServerError,
            ErrorMessages.Controller.Article.DeleteFailed
		));
	}

	/// <summary>
	/// 置顶文章
	/// </summary>
	/// <param name="articleId">文章ID</param>
	/// <returns>置顶结果</returns>
	[HttpPatch("pin/{articleId:Guid}", Name = "PinArticle")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status400BadRequest)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status404NotFound)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> Pin(Guid articleId)
	{
		var article  = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		if(User.GetUserId() != article.CreatorId)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Article.UserNotAuthorized
			));
		}
		
		if(article.IsPinned)
		{
			return BadRequest(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status400BadRequest,
				ErrorMessages.Controller.Article.ArticlePinned
			));
		}

		article.IsPinned = true;
		article.PinTime = DateTime.UtcNow;
		repository.ArticleRepository.Update(article);
		if(await repository.ArticleRepository.SaveAsync())
		{
			return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.PinSuccess));
		}

		logger.LogError("User {} pin article {} failed", User.GetUserId(), articleId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
            StatusCodes.Status500InternalServerError,
            ErrorMessages.Controller.Article.PinFailed
		));
	}

	/// <summary>
	/// 取消置顶
	/// </summary>
	/// <param name="articleId">文章ID</param>
	/// <returns>取消置顶结果</returns>
	[HttpPatch("cancel-pin/{articleId:Guid}", Name = "CanaelPinArticle")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status400BadRequest)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status404NotFound)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> CancelPin(Guid articleId)
	{
		var article  = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		if(User.GetUserId() != article.CreatorId)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Article.UserNotAuthorized
			));
		}

		if(!article.IsPinned)
		{
			return BadRequest(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status400BadRequest,
				ErrorMessages.Controller.Article.ArticleNotPinned
			));
		}

		article.IsPinned = false;
		article.PinTime = null;
		repository.ArticleRepository.Update(article);
		if(await repository.ArticleRepository.SaveAsync())
		{
			return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.PinCancelSuccess));
		}

		logger.LogError("User {} cancel pin article {} failed", User.GetUserId(), articleId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
            StatusCodes.Status500InternalServerError,
            ErrorMessages.Controller.Article.PinCancelFailed
		));
	}

	/// <summary>
	/// 获取文章列表
	/// </summary>
	/// <param name="len">列表长度</param>
	/// <param name="from">列表起始文章时间</param>
	/// <param name="reqType">类型</param>
	/// <param name="userId">用户ID</param>
	/// <returns>文章列表</returns>
	[HttpGet("list", Name = "List")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status400BadRequest)]
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status404NotFound)]	
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> GetArticleList([FromQuery] int len, [FromQuery] DateTime? from, [FromQuery] string? reqType, [FromQuery] Guid? userId = null)
	{
		if(reqType == "user" || reqType == "liked")
		{
			if(userId == null)
			{
				return BadRequest(ArticleListResp.Fail(
					StatusCodes.Status400BadRequest,
					ErrorMessages.Controller.Article.UserIdRequired
				));
			}
		}

		try
		{
			List<Guid>? articleIds = null;
			switch (reqType)
			{
				case "":
				case "all":
					articleIds = await repository.ArticleRepository.GetArticleListAsync(len, from);
					break;
				case "user":
					articleIds = await repository.ArticleRepository.GetArticleListByUserIdAsync(userId!.Value);
					break;
				case "liked":
					articleIds = await repository.UserArticleStatusRepository.GetArticleListByLikedRecordAsync(userId!.Value);
					break;
				default:
					return BadRequest(ArticleListResp.Fail(
						StatusCodes.Status400BadRequest,
						ErrorMessages.Controller.Article.InvalidReqType
					));
			}
			if(articleIds == null)
			{
				return NotFound(ArticleListResp.Fail(
					StatusCodes.Status404NotFound,
					ErrorMessages.Controller.Article.ArticleListNotFound
				));
			}

			return Ok(ArticleListResp.Success(articleIds));
		}
		catch (Exception e)
		{
			logger.LogError(e, "Fail to get article list");
			return StatusCode(StatusCodes.Status500InternalServerError, ArticleListResp.Fail(
				StatusCodes.Status500InternalServerError,
				ErrorMessages.Controller.Article.GetArticleListFailed
			));
		}
	}

	/// <summary>
	/// 获取文章详情
	/// </summary>
	/// <param name="articleId">文章ID</param>
	/// <returns>文章详情</returns>
	[HttpGet("detail/{articleId:Guid}", Name = "Detail")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[ProducesResponseType(typeof(ArticleDetailResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(ArticleDetailResp), StatusCodes.Status404NotFound)]
	public async Task<IActionResult> GetArticleDetail(Guid articleId)
	{
		var article = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ArticleDetailResp.Fail(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		var articleLikedCount = await repository.UserArticleStatusRepository.GetUserCountByLikedRecordAsync(articleId);

		return Ok(ArticleDetailResp.Success(article, articleLikedCount));
	}

	/// <summary>
	/// 点赞文章
	/// </summary>
	/// <param name="articleId">文章ID</param>
	/// <returns>点赞结果</returns>
	[HttpPost("like/{articleId:Guid}", Name = "Like")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.User)},{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status404NotFound)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> LikeArticle(Guid articleId)
	{
		var userId = User.GetUserId();
		if(userId == null)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Any.InvalidJwtToken
			));
		}

		var article = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		var likedArticle = await repository.UserArticleStatusRepository.GetArticleByLikedRecordAsync(userId.Value, articleId);
		if(likedArticle != null)
		{
			return BadRequest(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status400BadRequest,
				ErrorMessages.Controller.Article.ArticleLiked
			));
		}

		var UserArticleStatus = new UserArticleStatusEntity
		{
			UserId = userId.Value,
			LikedArticleId = articleId,
			LikedTime = DateTime.UtcNow
		};

		repository.UserArticleStatusRepository.Create(UserArticleStatus);
		if(await repository.UserArticleStatusRepository.SaveAsync())
		{
			return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.LikeSuccess));
		}

		logger.LogError("User {} Fail to like article {}", userId, articleId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
			StatusCodes.Status500InternalServerError,
			ErrorMessages.Controller.Article.LikeFailed
		));
	}

	/// <summary>
	/// 取消点赞文章
	/// </summary>
	/// <param name="articleId">文章ID</param>
	/// <returns>取消点赞结果</returns>
	[HttpPost("cancel-like/{articleId:Guid}", Name = "CancelLike")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[Authorize(Roles = $"{nameof(UserRole.User)},{nameof(UserRole.Creator)},{nameof(UserRole.Admin)},{nameof(UserRole.SuperAdmin)}")]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status403Forbidden)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status404NotFound)]
	[ProducesResponseType(typeof(BaseResp), StatusCodes.Status500InternalServerError)]
	public async Task<IActionResult> CancelLikeArticle(Guid articleId)
	{
		var userId = User.GetUserId();
		if(userId == null)
		{
			return StatusCode(StatusCodes.Status403Forbidden, ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status403Forbidden,
				ErrorMessages.Controller.Any.InvalidJwtToken
			));
		}

		var article = await repository.ArticleRepository.GetByIdAsync(articleId);
		if(article == null)
		{
			return NotFound(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}

		var likedArticle = await repository.UserArticleStatusRepository.GetArticleByLikedRecordAsync(userId.Value, articleId);
		if(likedArticle == null)
		{	
			return BadRequest(ResponseFactory.NewFailedBaseResponse(
				StatusCodes.Status400BadRequest,
				ErrorMessages.Controller.Article.ArticleNotLiked
			));
		}

		repository.UserArticleStatusRepository.Delete(likedArticle);
		if(await repository.UserArticleStatusRepository.SaveAsync())
		{
			return Ok(ResponseFactory.NewSuccessBaseResponse(SuccessMessages.Controller.Article.LikeSuccess));
		}

		logger.LogError("User {} Fail to cancel like article {}", userId, articleId);
		return StatusCode(StatusCodes.Status500InternalServerError, ResponseFactory.NewFailedBaseResponse(
			StatusCodes.Status500InternalServerError,
			ErrorMessages.Controller.Article.LikeFailed
		));
	}

	/// <summary>
	/// 搜索文章
	/// </summary>
	/// <param name="keyword">搜索关键字</param>
	/// <param name="page">起始页</param>
	/// <param name="pageSize">每页文章列表数目</param>
	/// <returns>查询结果</returns>
	[HttpGet("search", Name = "Search")]
	[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status200OK)]
	[ProducesResponseType(typeof(ArticleListResp), StatusCodes.Status404NotFound)]
	public async Task<IActionResult> SearchArticle([FromQuery] string keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
	{
		var articleIdList = await articleSearch.ArticleSearchAsync(keyword, page, pageSize);
		if(articleIdList == null)
		{
			return NotFound(ArticleListResp.Fail(
				StatusCodes.Status404NotFound,
				ErrorMessages.Controller.Article.ArticleNotFound
			));
		}
		return Ok(ArticleListResp.Success(articleIdList));
	}
}

