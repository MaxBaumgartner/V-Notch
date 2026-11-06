#include "vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services
{

class SpotlightUsageStore final
{
public:
    // The usage boost itself lives in Services/Spotlight/SpotlightUsageStore.cs.
    static bool IsAllowedApplicationHistoryTarget(
        std::u16string_view target,
        std::u16string_view start_menu_root,
        std::u16string_view common_start_menu_root) noexcept
    {
        if (vnotch::starts_with_ignore_case(target, u"shell:AppsFolder\\"))
        {
            return true;
        }
        auto check_root = [&](std::u16string_view root) noexcept -> bool
        {
            if (root.empty() || target.size() <= root.size())
            {
                return false;
            }
            return vnotch::starts_with_ignore_case(target, root) &&
                   target[root.size()] == u'\\';
        };
        return check_root(start_menu_root) || check_root(common_start_menu_root);
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotlight_usage_is_allowed_app_history_target(
    const char16_t* target_ptr, int32_t target_len,
    const char16_t* start_menu_ptr, int32_t start_menu_len,
    const char16_t* common_start_menu_ptr, int32_t common_start_menu_len,
    uint8_t* out_allowed) noexcept
{
    if (out_allowed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_allowed = vnotch::services::SpotlightUsageStore::IsAllowedApplicationHistoryTarget(
        vnotch::make_u16_view(target_ptr, target_len),
        vnotch::make_u16_view(start_menu_ptr, start_menu_len),
        vnotch::make_u16_view(common_start_menu_ptr, common_start_menu_len)) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
