#pragma once

#include <cstddef>
#include <cstdint>

namespace rt::xmem {

bool commit_physical_range(const void* address, std::size_t size);
bool commit_physical_pages(const std::uint64_t* page_array, std::size_t count);

}
