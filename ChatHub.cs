using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

public class UserProfile
{
    public string Name { get; set; } = "";
    public string AvatarType { get; set; } = "emoji"; // emoji or custom
    public string AvatarData { get; set; } = "👦🏻"; // emoji character or base64 image data
}

public class Message
{
    public string Type { get; set; } = "text"; // text, image, youtube, file, location
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Url { get; set; }
    public string? Filename { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Address { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ConnectionId { get; set; } = "";
    public string? AvatarType { get; set; }
    public string? AvatarData { get; set; }
}

// Spam prevention data structure
public class UserMessageInfo
{
    public Queue<DateTime> MessageTimestamps { get; set; } = new Queue<DateTime>();
    public List<string> RecentMessages { get; set; } = new List<string>();
    public DateTime? BlockedUntil { get; set; } = null;
    public string BlockReason { get; set; } = "";
}

public class ChatHub : Hub
{
    private static int count = 0;
    private static List<string> names = [];
    // Message history storage
    private static readonly List<Message> messageHistory = [];
    private static readonly int maxHistorySize = 50;

    // Spam prevention
    private static readonly ConcurrentDictionary<string, UserMessageInfo> userMessageTracking = new();
    private static readonly int messageRateLimit = 3;         // 3 messages
    private static readonly int messageRateWindowSeconds = 10; // per 10 seconds
    private static readonly int identicalMessageLimit = 3;     // 3 identical messages
    private static readonly int blockDurationSeconds = 10;     // block for 10 seconds
    private static readonly ConcurrentDictionary<string, string> connectionToName = new();
    
    // User profiles - map username to profile data
    private static readonly ConcurrentDictionary<string, UserProfile> userProfiles = new();

    // Helper method to add message to history
    private void AddToHistory(Message message)
    {
        lock (messageHistory)
        {
            messageHistory.Add(message);
            // Keep only the last 50 messages
            if (messageHistory.Count > maxHistorySize)
            {
                messageHistory.RemoveAt(0);
            }
        }
    }

    // Get user profile by name
    private UserProfile? GetUserProfile(string name)
    {
        if (userProfiles.TryGetValue(name, out var profile))
        {
            return profile;
        }
        return null;
    }

    // In ChatHub.cs, modify the GetMessageHistory method:
    public async Task GetMessageHistory()
    {
        string currentUserName = Context.GetHttpContext()!.Request.Query["name"].ToString();

        foreach (var message in messageHistory)
        {
            // Determine if current user was the original sender
            string source = (message.ConnectionId == Context.ConnectionId) ? "caller" : "others";

            switch (message.Type)
            {
                case "text":
                    await Clients.Caller.SendAsync("ReceiveText", message.Name, message.Content, source, message.Timestamp, message.AvatarType, message.AvatarData);
                    break;
                case "image":
                    await Clients.Caller.SendAsync("ReceiveImage", message.Name, message.Url, source, message.Timestamp, message.AvatarType, message.AvatarData);
                    break;
                case "youtube":
                    await Clients.Caller.SendAsync("ReceiveYouTube", message.Name, message.Content, source, message.Timestamp, message.AvatarType, message.AvatarData);
                    break;
                case "file":
                    await Clients.Caller.SendAsync("ReceiveFile", message.Name, message.Url, message.Filename, source, message.Timestamp, message.AvatarType, message.AvatarData);
                    break;
                case "location":
                    await Clients.Caller.SendAsync("ReceiveLocation", message.Name, message.Latitude ?? 0, message.Longitude ?? 0, message.Address ?? "", source, message.Timestamp, message.AvatarType, message.AvatarData);
                    break;
            }
        }
    }

    // Check if a user is currently blocked from sending messages
    private bool IsUserBlocked(string userId, out TimeSpan remainingTime, out string reason)
    {
        remainingTime = TimeSpan.Zero;
        reason = "";

        if (!userMessageTracking.TryGetValue(userId, out var userInfo) || userInfo.BlockedUntil == null)
        {
            return false;
        }

        if (userInfo.BlockedUntil > DateTime.Now)
        {
            remainingTime = userInfo.BlockedUntil.Value - DateTime.Now;
            reason = userInfo.BlockReason;
            return true;
        }

        // Reset block if expired
        userInfo.BlockedUntil = null;
        userInfo.BlockReason = "";
        return false;
    }

    // Check message rate limit (3 messages per 10 seconds)
    private bool IsRateLimited(string userId)
    {
        if (!userMessageTracking.TryGetValue(userId, out var userInfo))
        {
            userInfo = new UserMessageInfo();
            userMessageTracking[userId] = userInfo;
        }

        // Clean up timestamps older than the rate window
        DateTime cutoff = DateTime.Now.AddSeconds(-messageRateWindowSeconds);
        while (userInfo.MessageTimestamps.Count > 0 && userInfo.MessageTimestamps.Peek() < cutoff)
        {
            userInfo.MessageTimestamps.Dequeue();
        }

        // Check if rate limited
        if (userInfo.MessageTimestamps.Count >= messageRateLimit)
        {
            // Block the user
            userInfo.BlockedUntil = DateTime.Now.AddSeconds(blockDurationSeconds);
            userInfo.BlockReason = $"Rate limit exceeded: maximum {messageRateLimit} messages per {messageRateWindowSeconds} seconds";
            return true;
        }

        return false;
    }

    // Check for consecutive identical messages
    private bool HasConsecutiveIdenticalMessages(string userId, string message)
    {
        if (!userMessageTracking.TryGetValue(userId, out var userInfo))
        {
            userInfo = new UserMessageInfo();
            userMessageTracking[userId] = userInfo;
        }

        // Add message to user's recent messages
        userInfo.RecentMessages.Add(message);

        // Keep only the last N messages
        if (userInfo.RecentMessages.Count > identicalMessageLimit)
        {
            userInfo.RecentMessages.RemoveAt(0);
        }

        // Check if we have enough messages to evaluate
        if (userInfo.RecentMessages.Count < identicalMessageLimit)
        {
            return false;
        }

        // Check if all recent messages are identical
        string firstMessage = userInfo.RecentMessages[0];
        bool allIdentical = userInfo.RecentMessages.All(m => m == firstMessage);

        if (allIdentical)
        {
            // Block the user
            userInfo.BlockedUntil = DateTime.Now.AddSeconds(blockDurationSeconds);
            userInfo.BlockReason = $"Spam detected: {identicalMessageLimit} identical consecutive messages";

            // Clear recent messages so they start fresh after the block
            userInfo.RecentMessages.Clear();
            return true;
        }

        return false;
    }

    // Track a new message for a user
    private void TrackUserMessage(string userId, string message)
    {
        if (!userMessageTracking.TryGetValue(userId, out var userInfo))
        {
            userInfo = new UserMessageInfo();
            userMessageTracking[userId] = userInfo;
        }

        userInfo.MessageTimestamps.Enqueue(DateTime.Now);
    }

    public async Task SendText(string name, string message)
    {
        string userId = Context.ConnectionId;
        var timestamp = DateTime.Now;

        // Check if user is blocked
        if (IsUserBlocked(userId, out TimeSpan remainingTime, out string blockReason))
        {
            // Notify only the caller about being blocked with remaining time
            await Clients.Caller.SendAsync("MessageBlocked", blockReason, Math.Ceiling(remainingTime.TotalSeconds));
            return;
        }

        // Check rate limiting
        if (IsRateLimited(userId))
        {
            await Clients.Caller.SendAsync("MessageBlocked",
                $"Rate limit exceeded: maximum {messageRateLimit} messages per {messageRateWindowSeconds} seconds",
                blockDurationSeconds);
            return;
        }

        // Check for consecutive identical messages
        if (HasConsecutiveIdenticalMessages(userId, message))
        {
            await Clients.Caller.SendAsync("MessageBlocked",
                $"Spam detected: {identicalMessageLimit} identical consecutive messages",
                blockDurationSeconds);
            return;
        }

        // Track this message
        TrackUserMessage(userId, message);

        // Get user profile
        var userProfile = GetUserProfile(name);

        // Add to history
        AddToHistory(new Message
        {
            Type = "text",
            Name = name,
            Content = message,
            ConnectionId = Context.ConnectionId,
            Timestamp = timestamp,
            AvatarType = userProfile?.AvatarType,
            AvatarData = userProfile?.AvatarData
        });

        await Clients.Caller.SendAsync("ReceiveText", name, message, "caller", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
        await Clients.Others.SendAsync("ReceiveText", name, message, "others", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
    }

    public async Task SendImage(string name, string url)
    {
        string userId = Context.ConnectionId;
        var timestamp = DateTime.Now;

        // Check if user is blocked
        if (IsUserBlocked(userId, out TimeSpan remainingTime, out string blockReason))
        {
            await Clients.Caller.SendAsync("MessageBlocked", blockReason, Math.Ceiling(remainingTime.TotalSeconds));
            return;
        }

        // Get user profile
        var userProfile = GetUserProfile(name);

        // Add to history
        AddToHistory(new Message
        {
            Type = "image",
            Name = name,
            ConnectionId = Context.ConnectionId,
            Url = url,
            Timestamp = timestamp,
            AvatarType = userProfile?.AvatarType,
            AvatarData = userProfile?.AvatarData
        });

        await Clients.Caller.SendAsync("ReceiveImage", name, url, "caller", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
        await Clients.Others.SendAsync("ReceiveImage", name, url, "others", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
    }

    public async Task SendYouTube(string name, string id)
    {
        string userId = Context.ConnectionId;
        var timestamp = DateTime.Now;

        // Check if user is blocked
        if (IsUserBlocked(userId, out TimeSpan remainingTime, out string blockReason))
        {
            await Clients.Caller.SendAsync("MessageBlocked", blockReason, Math.Ceiling(remainingTime.TotalSeconds));
            return;
        }

        // Get user profile
        var userProfile = GetUserProfile(name);

        // Add to history
        AddToHistory(new Message
        {
            Type = "youtube",
            Name = name,
            ConnectionId = Context.ConnectionId,
            Content = id,
            Timestamp = timestamp,
            AvatarType = userProfile?.AvatarType,
            AvatarData = userProfile?.AvatarData
        });

        await Clients.Caller.SendAsync("ReceiveYouTube", name, id, "caller", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
        await Clients.Others.SendAsync("ReceiveYouTube", name, id, "others", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
    }

    public async Task SendFile(string name, string url, string filename)
    {
        string userId = Context.ConnectionId;
        var timestamp = DateTime.Now;

        // Check if user is blocked
        if (IsUserBlocked(userId, out TimeSpan remainingTime, out string blockReason))
        {
            await Clients.Caller.SendAsync("MessageBlocked", blockReason, Math.Ceiling(remainingTime.TotalSeconds));
            return;
        }

        // Get user profile
        var userProfile = GetUserProfile(name);

        // Add to history
        AddToHistory(new Message
        {
            Type = "file",
            Name = name,
            ConnectionId = Context.ConnectionId,
            Url = url,
            Filename = filename,
            Timestamp = timestamp,
            AvatarType = userProfile?.AvatarType,
            AvatarData = userProfile?.AvatarData
        });

        await Clients.Caller.SendAsync("ReceiveFile", name, url, filename, "caller", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
        await Clients.Others.SendAsync("ReceiveFile", name, url, filename, "others", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
    }

    public async Task SendLocation(string name, double latitude, double longitude, string address)
    {
        string userId = Context.ConnectionId;
        var timestamp = DateTime.Now;

        // Check if user is blocked
        if (IsUserBlocked(userId, out TimeSpan remainingTime, out string blockReason))
        {
            await Clients.Caller.SendAsync("MessageBlocked", blockReason, Math.Ceiling(remainingTime.TotalSeconds));
            return;
        }

        // Get user profile
        var userProfile = GetUserProfile(name);

        // Add to history
        AddToHistory(new Message
        {
            Type = "location",
            Name = name,
            ConnectionId = Context.ConnectionId,
            Latitude = latitude,
            Longitude = longitude,
            Address = address,
            Timestamp = timestamp,
            AvatarType = userProfile?.AvatarType,
            AvatarData = userProfile?.AvatarData
        });

        await Clients.Caller.SendAsync("ReceiveLocation", name, latitude, longitude, address, "caller", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
        await Clients.Others.SendAsync("ReceiveLocation", name, latitude, longitude, address, "others", timestamp, userProfile?.AvatarType, userProfile?.AvatarData);
    }

    // API method to check if username is available
    [Microsoft.AspNetCore.Mvc.HttpGet("/api/checkUsername")]
    public Microsoft.AspNetCore.Mvc.IActionResult CheckUsername([Microsoft.AspNetCore.Mvc.FromQuery] string name)
    {
        bool isAvailable = !names.Contains(name);
        return new Microsoft.AspNetCore.Mvc.JsonResult(new { available = isAvailable });
    }

    // Add method to update user avatar
    public async Task UpdateAvatar(string name, string avatarType, string avatarData)
    {
        if (userProfiles.TryGetValue(name, out var profile))
        {
            profile.AvatarType = avatarType;
            profile.AvatarData = avatarData;
        }
    }

    public override async Task OnConnectedAsync()
    {
        count++;
        string name = Context.GetHttpContext()!.Request.Query["name"].ToString();
        string avatarType = Context.GetHttpContext()!.Request.Query["avatarType"].ToString() ?? "emoji";
        string avatarData = Context.GetHttpContext()!.Request.Query["avatarData"].ToString() ?? "👦🏻";
        
        // Check if name already exists
        if (names.Contains(name))
        {
            // Disconnect the client
            Context.Abort();
            return;
        }
        
        names.Add(name);
        connectionToName[Context.ConnectionId] = name;
        
        // Store user profile
        userProfiles[name] = new UserProfile
        {
            Name = name,
            AvatarType = avatarType,
            AvatarData = avatarData
        };

        // First send the welcome message
        await Clients.All.SendAsync("UpdateStatus", count, $"<b>{name}</b> joined", names);

        // Then send message history to the new user
        await GetMessageHistory();

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        count--;
        string name = Context.GetHttpContext()!.Request.Query["name"].ToString();
        names.Remove(name);
        connectionToName.TryRemove(Context.ConnectionId, out _);
        
        // Remove user profile
        userProfiles.TryRemove(name, out _);

        // Clean up user's spam prevention data
        userMessageTracking.TryRemove(Context.ConnectionId, out _);

        await Clients.All.SendAsync("UpdateStatus", count, $"<b>{name}</b> left", names);
        await base.OnDisconnectedAsync(exception);
    }
}